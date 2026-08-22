using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrderWatchLite.Services
{
    /// <summary>
    /// Binance USD-M Futures API 服务层
    /// </summary>
    public class BinanceApiService : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly bool _useTestNet;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _httpClient = new HttpClient();

        // ============================================================
        // ExchangeInfo 缓存
        // ============================================================

        private ExchangeInfoCache? _exchangeInfoCache;
        private readonly SemaphoreSlim _exchangeInfoLock = new(1, 1);
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);
        private readonly Dictionary<string, decimal> _priceTickCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _priceTickLock = new();

        // ============================================================
        // 保护单本地映射
        //
        // OrderId -> 保护单信息
        // ============================================================

        private readonly Dictionary<long, StopOrderInfo> _stopOrders = new();
        private readonly object _stopOrderLock = new();

        // ============================================================
        // 构造
        // ============================================================

        public BinanceApiService(
            string apiKey,
            string apiSecret,
            bool useTestNet = true)
        {
            _useTestNet = useTestNet;
            _apiKey = apiKey;
            _apiSecret = apiSecret;

            _restClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials =
                    new BinanceCredentials(apiKey, apiSecret);

                options.Environment = useTestNet
                    ? BinanceEnvironment.Testnet
                    : BinanceEnvironment.Live;
            });
        }

        // ============================================================
        // 交易对列表
        // ============================================================

        public async Task<List<string>> GetAllSymbolsAsync()
        {
            try
            {
                var exchangeInfo = await GetExchangeInfoAsync();

                return exchangeInfo?.Symbols
                    .Where(x =>
                        x.Status == SymbolStatus.Trading &&
                        x.Name.EndsWith("USDT"))
                    .Select(x => x.Name)
                    .OrderBy(x => x)
                    .ToList()
                    ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ============================================================
        // 当前价格
        // ============================================================

        public async Task<decimal?> GetCurrentPriceAsync(string symbol)
        {
            try
            {
                var result =
                    await _restClient.UsdFuturesApi.ExchangeData
                        .GetTickerAsync(symbol);

                if (result.Success && result.Data != null)
                    return result.Data.LastPrice;

                return null;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // 账户余额
        // ============================================================

        public async Task<decimal?> GetAccountBalanceAsync()
        {
            try
            {
                var result =
                    await _restClient.UsdFuturesApi.Account
                        .GetAccountInfoV3Async();

                if (!result.Success || result.Data == null)
                    return null;

                var asset = result.Data.Assets
                    .FirstOrDefault(a =>
                        a.Asset.Equals(
                            "USDT",
                            StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                    return 0m;

                return asset.WalletBalance;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // 获取数量步长
        // ============================================================

        public async Task<LotSizeInfo?> GetLotSizeInfoAsync(string symbol)
        {
            var exchangeInfo = await GetExchangeInfoAsync();

            if (exchangeInfo == null)
                return null;

            var symbolData = exchangeInfo.Symbols
                .FirstOrDefault(s =>
                    s.Name.Equals(
                        symbol,
                        StringComparison.OrdinalIgnoreCase));

            if (symbolData == null)
                return null;

            var lotFilter = symbolData.LotSizeFilter;

            if (lotFilter == null)
                return null;

            return new LotSizeInfo
            {
                StepSize = lotFilter.StepSize,
                MinQty = lotFilter.MinQuantity,
                MaxQty = lotFilter.MaxQuantity
            };
        }

        // ============================================================
        // 主单 + 保护止损
        //
        // 市价开仓
        // ↓
        // 成交
        // ↓
        // Reduce-Only STOP-MARKET
        // ============================================================

        public async Task<(
            bool success,
            string orderId,
            string stopOrderId,
            string error)>
            PlaceOrderWithStopLossAsync(
                string symbol,
                OrderSide side,
                decimal quantity,
                decimal stopPrice)
        {
            try
            {
                if (quantity <= 0)
                {
                    return (
                        false,
                        string.Empty,
                        string.Empty,
                        "下单数量必须大于 0");
                }

                if (stopPrice <= 0)
                {
                    return (
                        false,
                        string.Empty,
                        string.Empty,
                        "止损价格必须大于 0");
                }

                // ====================================================
                // 1. 市价主单
                // ====================================================

                var mainResult =
                    await _restClient.UsdFuturesApi.Trading
                        .PlaceOrderAsync(
                            symbol: symbol,
                            side: side,
                            type: FuturesOrderType.Market,
                            quantity: quantity,
                            newClientOrderId:
                                Guid.NewGuid().ToString("N"));

                if (!mainResult.Success ||
                    mainResult.Data == null)
                {
                    return (
                        false,
                        string.Empty,
                        string.Empty,
                        mainResult.Error?.Message ??
                        "主单下单失败");
                }

                long mainOrderId = mainResult.Data.Id;

                // ====================================================
                // 2. 创建独立 Reduce-Only STOP-MARKET
                // ====================================================

                var stopResult =
                    await PlaceReduceOnlyStopMarketInternalAsync(
                        symbol,
                        side,
                        quantity,
                        stopPrice);

                if (!stopResult.success)
                {
                    return (
                        true,
                        mainOrderId.ToString(),
                        string.Empty,
                        $"主单成功但保护单失败: {stopResult.error}");
                }

                return (
                    true,
                    mainOrderId.ToString(),
                    stopResult.orderId,
                    string.Empty);
            }
            catch (Exception ex)
            {
                return (
                    false,
                    string.Empty,
                    string.Empty,
                    ex.Message);
            }
        }

        // ============================================================
        // 创建独立 Reduce-Only STOP-MARKET
        //
        // side 表示当前持仓方向：
        //
        // BUY  = 多仓
        // SELL = 空仓
        // ============================================================

        public async Task<(
            bool success,
            string orderId,
            string error)>
            PlaceReduceOnlyStopMarketAsync(
                string symbol,
                OrderSide side,
                decimal quantity,
                decimal stopPrice)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    return (
                        false,
                        string.Empty,
                        "交易对不能为空");
                }

                if (quantity <= 0)
                {
                    return (
                        false,
                        string.Empty,
                        "保护单数量必须大于 0");
                }

                if (stopPrice <= 0)
                {
                    return (
                        false,
                        string.Empty,
                        "保护价必须大于 0");
                }

                return await PlaceReduceOnlyStopMarketInternalAsync(
                    symbol,
                    side,
                    quantity,
                    stopPrice);
            }
            catch (Exception ex)
            {
                return (
                    false,
                    string.Empty,
                    ex.Message);
            }
        }

        // ============================================================
        // 内部真正创建保护单的方法
        // ============================================================

        private async Task<(
            bool success,
            string orderId,
            string error)>
            PlaceReduceOnlyStopMarketInternalAsync(
                string symbol, OrderSide side, decimal quantity, decimal stopPrice)
        {
            try
            {
                // Binance Futures 已将 STOP_MARKET 条件单迁移到 Algo Order 接口。
                // 这里必须同时按照 Binance 的数量步长和价格 TickSize 规范化。
                // 尤其是“加仓保护单”：加仓成交价经过加权反推后，往往会产生很多小数位，
                // 如果直接把这个价格交给 Binance，就会出现 -1111 Precision is over the maximum defined for this asset。
                decimal normalizedQuantity = await NormalizeQuantityAsync(symbol, quantity);
                if (normalizedQuantity <= 0)
                    return (false, string.Empty, "保护单数量经过交易所步长处理后为 0");

                decimal normalizedStopPrice = await NormalizePriceAsync(symbol, stopPrice, side);
                if (normalizedStopPrice <= 0)
                    return (false, string.Empty, "保护单价格经过交易所价格精度处理后无效");

                OrderSide closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                string baseUrl = _useTestNet ? "https://testnet.binancefuture.com" : "https://fapi.binance.com";
                var parameters = new SortedDictionary<string, string>
                {
                    ["algoType"] = "CONDITIONAL",
                    ["symbol"] = symbol,
                    ["side"] = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                    ["type"] = "STOP_MARKET",
                    ["quantity"] = normalizedQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["triggerPrice"] = normalizedStopPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["workingType"] = "MARK_PRICE",
                    ["reduceOnly"] = "true",
                    ["clientAlgoId"] = $"SL_{Guid.NewGuid():N}".Substring(0, 32),
                    ["recvWindow"] = "5000",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
                };
                string query = string.Join("&", parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
                string signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/fapi/v1/algoOrder?{query}&signature={signature}");
                request.Headers.Add("X-MBX-APIKEY", _apiKey);
                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    string error = body;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(body);
                        string msg = doc.RootElement.TryGetProperty("msg", out var m) ? (m.GetString() ?? body) : body;
                        string code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32().ToString() : "?";
                        error = $"Binance错误 {code}: {msg}";
                    } catch { }
                    return (false, string.Empty, error);
                }
                using JsonDocument json = JsonDocument.Parse(body);
                long orderId = json.RootElement.TryGetProperty("algoId", out var idElement) ? idElement.GetInt64() : 0;
                if (orderId <= 0) return (false, string.Empty, $"保护单创建成功但未返回有效订单ID: {body}");
                lock (_stopOrderLock)
                {
                    _stopOrders[orderId] = new StopOrderInfo { OrderId = orderId, Symbol = symbol, PositionSide = side, Quantity = normalizedQuantity, StopPrice = normalizedStopPrice };
                }
                return (true, orderId.ToString(), string.Empty);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, ex.Message);
            }
        }

        // ============================================================
        // 修改保护单
        //
        // Binance 没有直接修改原 STOP-MARKET 的逻辑：
        //
        // 原保护单
        //      ↓
        // 撤销
        //      ↓
        // 创建新的保护单
        //
        // 返回：
        //
        // success    是否成功
        // newOrderId 新保护单 ID
        // error      错误信息
        // ============================================================

        public async Task<(
            bool success,
            long newOrderId,
            string error)>
            ModifyStopLossAsync(
                long stopLossOrderId,
                decimal newStopPrice)
        {
            try
            {
                if (stopLossOrderId <= 0)
                {
                    return (
                        false,
                        0,
                        "原保护单ID无效");
                }

                if (newStopPrice <= 0)
                {
                    return (
                        false,
                        0,
                        "新的止损价格必须大于 0");
                }

                StopOrderInfo? oldOrder = null;

                lock (_stopOrderLock)
                {
                    _stopOrders.TryGetValue(
                        stopLossOrderId,
                        out oldOrder);
                }

                // 如果程序内部没有找到原保护单，
                // 不猜 symbol / direction / quantity。
                if (oldOrder == null)
                {
                    return (
                        false,
                        0,
                        $"找不到保护单 {stopLossOrderId} 的本地记录");
                }

                // ====================================================
                // 1. 撤销旧保护单
                // ====================================================

                var cancelResult =
                    await _restClient.UsdFuturesApi.Trading
                        .CancelOrderAsync(
                            oldOrder.Symbol,
                            orderId: stopLossOrderId);

                if (!cancelResult.Success)
                {
                    return (
                        false,
                        0,
                        cancelResult.Error?.Message ??
                        "撤销旧保护单失败");
                }

                // ====================================================
                // 2. 创建新的 Reduce-Only STOP-MARKET
                // ====================================================

                var newOrder =
                    await PlaceReduceOnlyStopMarketInternalAsync(
                        oldOrder.Symbol,
                        oldOrder.PositionSide,
                        oldOrder.Quantity,
                        newStopPrice);

                if (!newOrder.success)
                {
                    return (
                        false,
                        0,
                        $"新保护单创建失败: {newOrder.error}");
                }

                long newOrderId = 0;

                if (!long.TryParse(
                    newOrder.orderId,
                    out newOrderId)
                    ||
                    newOrderId <= 0)
                {
                    return (
                        false,
                        0,
                        "新保护单创建成功，但订单ID无效");
                }

                // ====================================================
                // 3. 删除旧保护单映射
                //
                // 新保护单已经在内部方法中登记。
                // ====================================================

                lock (_stopOrderLock)
                {
                    _stopOrders.Remove(stopLossOrderId);
                }

                return (
                    true,
                    newOrderId,
                    string.Empty);
            }
            catch (Exception ex)
            {
                return (
                    false,
                    0,
                    ex.Message);
            }
        }

        // ============================================================
        // 删除保护单
        // ============================================================

        public async Task<bool> CancelStopLossAsync(
            long stopLossOrderId)
        {
            try
            {
                StopOrderInfo? info = null;

                lock (_stopOrderLock)
                {
                    _stopOrders.TryGetValue(
                        stopLossOrderId,
                        out info);
                }

                if (info == null)
                    return false;

                var result =
                    await _restClient.UsdFuturesApi.Trading
                        .CancelOrderAsync(
                            info.Symbol,
                            orderId: stopLossOrderId);

                if (!result.Success)
                    return false;

                lock (_stopOrderLock)
                {
                    _stopOrders.Remove(stopLossOrderId);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ============================================================
        // 平仓
        // ============================================================

        public async Task<(
            bool success,
            string orderId,
            string error)>
            ClosePositionAsync(
                string symbol,
                decimal quantity,
                OrderSide closeSide)
        {
            try
            {
                if (quantity <= 0)
                {
                    return (
                        false,
                        string.Empty,
                        "平仓数量必须大于 0");
                }

                var result =
                    await _restClient.UsdFuturesApi.Trading
                        .PlaceOrderAsync(
                            symbol: symbol,
                            side: closeSide,
                            type: FuturesOrderType.Market,
                            quantity: quantity,
                            newClientOrderId:
                                $"CLOSE_{Guid.NewGuid():N}"
                                    .Substring(0, 32),
                            reduceOnly: true);

                if (!result.Success ||
                    result.Data == null)
                {
                    return (
                        false,
                        string.Empty,
                        result.Error?.Message ??
                        "平仓失败");
                }

                return (
                    true,
                    result.Data.Id.ToString(),
                    string.Empty);
            }
            catch (Exception ex)
            {
                return (
                    false,
                    string.Empty,
                    ex.Message);
            }
        }

        // ============================================================
        // 获取实际持仓
        // ============================================================

        public async Task<List<PositionInfo>> GetPositionsAsync()
        {
            try
            {
                var result =
                    await _restClient.UsdFuturesApi.Trading
                        .GetPositionsAsync();

                if (!result.Success ||
                    result.Data == null)
                {
                    return new List<PositionInfo>();
                }

                var positions = new List<PositionInfo>();

                foreach (var p in result.Data)
                {
                    decimal positionAmt = p.PositionAmt;

                    if (positionAmt == 0)
                        continue;

                    decimal entryPrice = p.EntryPrice;
                    decimal markPrice = p.MarkPrice;

                    decimal quantity =
                        Math.Abs(positionAmt);

                    OrderSide side =
                        positionAmt > 0
                            ? OrderSide.Buy
                            : OrderSide.Sell;

                    decimal unrealizedPnl = 0m;

                    if (entryPrice > 0 &&
                        markPrice > 0)
                    {
                        unrealizedPnl =
                            side == OrderSide.Buy
                                ? (markPrice - entryPrice)
                                    * quantity
                                : (entryPrice - markPrice)
                                    * quantity;
                    }

                    decimal pnlPercent = 0m;

                    if (entryPrice > 0)
                    {
                        pnlPercent =
                            side == OrderSide.Buy
                                ? (markPrice - entryPrice)
                                    / entryPrice * 100m
                                : (entryPrice - markPrice)
                                    / entryPrice * 100m;
                    }

                    int leverage = 0;

                    if (p.Leverage.HasValue)
                    {
                        leverage =
                            (int)Math.Round(
                                p.Leverage.Value,
                                MidpointRounding.AwayFromZero);
                    }

                    positions.Add(new PositionInfo
                    {
                        Symbol = p.Symbol,
                        Quantity = quantity,
                        Side = side,
                        EntryPrice = entryPrice,
                        MarkPrice = markPrice,
                        UnrealizedPnl = unrealizedPnl,
                        Leverage = leverage,
                        PnlPercent = pnlPercent
                    });
                }

                return positions;
            }
            catch
            {
                return new List<PositionInfo>();
            }
        }

        // ============================================================
        // Binance 数量/价格精度处理
        // ============================================================

        private async Task<decimal> NormalizeQuantityAsync(string symbol, decimal quantity)
        {
            var info = await GetLotSizeInfoAsync(symbol);
            if (info == null || info.StepSize <= 0)
                return quantity;

            decimal steps = Math.Floor(quantity / info.StepSize);
            decimal result = steps * info.StepSize;

            if (result < info.MinQty)
                return 0m;

            if (result > info.MaxQty)
                result = info.MaxQty;

            return result;
        }

        private async Task<decimal> NormalizePriceAsync(string symbol, decimal price, OrderSide positionSide)
        {
            if (price <= 0)
                return 0m;

            decimal tickSize = 0m;
            lock (_priceTickLock)
            {
                _priceTickCache.TryGetValue(symbol, out tickSize);
            }

            if (tickSize <= 0)
            {
                try
                {
                    string baseUrl = _useTestNet ? "https://testnet.binancefuture.com" : "https://fapi.binance.com";
                    string json = await _httpClient.GetStringAsync($"{baseUrl}/fapi/v1/exchangeInfo");
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (var item in doc.RootElement.GetProperty("symbols").EnumerateArray())
                    {
                        if (!item.TryGetProperty("symbol", out var sym) ||
                            !string.Equals(sym.GetString(), symbol, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (item.TryGetProperty("filters", out var filters))
                        {
                            foreach (var filter in filters.EnumerateArray())
                            {
                                if (filter.TryGetProperty("filterType", out var ft) &&
                                    ft.GetString() == "PRICE_FILTER" &&
                                    filter.TryGetProperty("tickSize", out var ts))
                                {
                                    decimal.TryParse(ts.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tickSize);
                                    break;
                                }
                            }
                        }
                        break;
                    }

                    if (tickSize > 0)
                    {
                        lock (_priceTickLock)
                            _priceTickCache[symbol] = tickSize;
                    }
                }
                catch
                {
                    // 如果交易所信息暂时获取失败，保留原价格；真正下单时 Binance 会返回明确错误。
                }
            }

            if (tickSize <= 0)
                return price;

            // 止损价格统一向交易所合法 TickSize 对齐。
            decimal steps = Math.Floor(price / tickSize);
            decimal result = steps * tickSize;
            return result > 0 ? result : price;
        }

        // ============================================================
        // ExchangeInfo
        // ============================================================

        private async Task<ExchangeInfoCache?>
            GetExchangeInfoAsync()
        {
            await _exchangeInfoLock.WaitAsync();

            try
            {
                if (_exchangeInfoCache != null &&
                    DateTime.UtcNow -
                    _exchangeInfoCache.FetchedAt <
                    _cacheExpiry)
                {
                    return _exchangeInfoCache;
                }

                var result =
                    await _restClient.UsdFuturesApi.ExchangeData
                        .GetExchangeInfoAsync();

                if (!result.Success ||
                    result.Data == null)
                {
                    return _exchangeInfoCache;
                }

                _exchangeInfoCache =
                    new ExchangeInfoCache
                    {
                        FetchedAt = DateTime.UtcNow,
                        Symbols =
                            result.Data.Symbols.ToList()
                    };

                return _exchangeInfoCache;
            }
            finally
            {
                _exchangeInfoLock.Release();
            }
        }

        // ============================================================
        // IDisposable
        // ============================================================

        public void Dispose()
        {
            _restClient.Dispose();
            _exchangeInfoLock.Dispose();
        }
    }

    // ================================================================
    // 保护单内部记录
    // ================================================================

    internal class StopOrderInfo
    {
        public long OrderId { get; set; }

        public string Symbol { get; set; } =
            string.Empty;

        public OrderSide PositionSide { get; set; }

        public decimal Quantity { get; set; }

        public decimal StopPrice { get; set; }
    }

    // ================================================================
    // ExchangeInfo 缓存
    // ================================================================

    internal class ExchangeInfoCache
    {
        public DateTime FetchedAt { get; set; }

        public List<
            Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtSymbol>
            Symbols
        { get; set; } = new();
    }

    // ================================================================
    // 数量规则
    // ================================================================

    public class LotSizeInfo
    {
        public decimal StepSize { get; set; }

        public decimal MinQty { get; set; }

        public decimal MaxQty { get; set; }
    }

    // ================================================================
    // UI 持仓模型
    // ================================================================

    public class PositionInfo
    {
        public string Symbol { get; set; } =
            string.Empty;

        public decimal Quantity { get; set; }

        public OrderSide Side { get; set; }

        public decimal EntryPrice { get; set; }

        public decimal MarkPrice { get; set; }

        public decimal UnrealizedPnl { get; set; }

        public int Leverage { get; set; }

        public decimal PnlPercent { get; set; }

        public string DisplayText { get; set; } =
            string.Empty;
    }
}