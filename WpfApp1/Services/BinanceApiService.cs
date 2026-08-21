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
        //
        // 用于 ModifyStopLossAsync：
        // MainWindow 只传 StopLossOrderId + 新止损价，
        // 这里负责找到原保护单对应的品种、方向、数量。
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
        // 新增：
        // PlaceReduceOnlyStopMarketAsync
        //
        // 给 PositionManager / MainWindow 使用。
        //
        // 每一个逻辑层都可以拥有自己的 STOP-MARKET。
        // ============================================================

        public async Task<(
            bool success,
            string orderId,
            string error)>
            PlaceReduceOnlyStopMarketAsync(
                string symbol,
                OrderSide positionSide,
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
                    positionSide,
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
                OrderSide positionSide,
                decimal quantity,
                decimal stopPrice)
        {
            try
            {
                // 持仓方向：
                //
                // BUY  = 多仓
                // SELL = 空仓
                //
                // 平多 -> SELL
                // 平空 -> BUY

                OrderSide closeSide =
                    positionSide == OrderSide.Buy
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
                        PositionSide = positionSide,
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
        // 注意：
        // 这里不是直接修改 STOP-MARKET。
        //
        // 逻辑：
        //
        // 原保护单
        //      ↓
        // 撤销
        //      ↓
        // 新保护单
        //
        // 所以保本后仍然是一个
        // Reduce-Only STOP-MARKET。
        // ============================================================

        public async Task<bool> ModifyStopLossAsync(
            long stopLossOrderId,
            decimal newStopPrice)
        {
            try
            {
                if (stopLossOrderId <= 0)
                    return false;

                if (newStopPrice <= 0)
                    return false;

                StopOrderInfo? oldOrder = null;

                lock (_stopOrderLock)
                {
                    _stopOrders.TryGetValue(
                        stopLossOrderId,
                        out oldOrder);
                }

                // 如果程序内缓存没有找到，
                // 暂时无法安全重建保护单。
                //
                // 不猜 symbol / direction / quantity。
                if (oldOrder == null)
                    return false;

                // ====================================================
                // 1. 撤销旧保护单
                // ====================================================

                var cancelResult =
                    await _restClient.UsdFuturesApi.Trading
                        .CancelOrderAsync(
                            oldOrder.Symbol,
                            orderId: stopLossOrderId);

                if (!cancelResult.Success)
                    return false;

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
                    return false;

                // ====================================================
                // 3. 删除旧映射
                // ====================================================

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