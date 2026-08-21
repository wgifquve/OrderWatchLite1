using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrderWatchLite.Services
{
    /// <summary>
    /// Binance USD-M Futures API 服务层
    /// </summary>
    public class BinanceApiService : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly bool _useTestNet;

        // ============================================================
        // ExchangeInfo 缓存
        // ============================================================

        private ExchangeInfoCache? _exchangeInfoCache;
        private readonly SemaphoreSlim _exchangeInfoLock = new(1, 1);
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

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
                string symbol,
                OrderSide side,
                decimal quantity,
                decimal stopPrice)
        {
            try
            {
                // 当前持仓方向：
                //
                // BUY  = 多仓
                // SELL = 空仓
                //
                // 平多 -> SELL
                // 平空 -> BUY

                OrderSide closeSide =
                    side == OrderSide.Buy
                        ? OrderSide.Sell
                        : OrderSide.Buy;

                var result =
                    await _restClient.UsdFuturesApi.Trading
                        .PlaceOrderAsync(
                            symbol: symbol,
                            side: closeSide,
                            type: FuturesOrderType.StopMarket,
                            quantity: quantity,
                            stopPrice: stopPrice,
                            timeInForce: TimeInForce.GoodTillCanceled,
                            newClientOrderId:
                                $"SL_{Guid.NewGuid():N}"
                                    .Substring(0, 32),
                            reduceOnly: true);

                if (!result.Success ||
                    result.Data == null)
                {
                    return (
                        false,
                        string.Empty,
                        result.Error?.Message ??
                        "保护单创建失败");
                }

                long orderId = result.Data.Id;

                // 保存保护单映射
                lock (_stopOrderLock)
                {
                    _stopOrders[orderId] = new StopOrderInfo
                    {
                        OrderId = orderId,
                        Symbol = symbol,
                        PositionSide = side,
                        Quantity = quantity,
                        StopPrice = stopPrice
                    };
                }

                return (
                    true,
                    orderId.ToString(),
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