using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects;
using Binance.Net.Objects.Models.Futures; // 补充了模型命名空间，确保兼容性
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace OrderWatchLite.Services
{
    /// <summary>
    /// Binance Futures API 服务
    /// Binance.Net 13.4 - 仅使用 Trading 端点
    /// </summary>
    public class BinanceApiService : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly bool _isTestNet;

        private decimal _walletBalance;
        private decimal _currentPrice;

        public event Action<decimal>? OnBalanceUpdated;
        public event Action<decimal>? OnPriceUpdated;

        public BinanceApiService(
            string apiKey,
            string apiSecret,
            bool isTestNet = true)
        {
            _isTestNet = isTestNet;

            _restClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new BinanceCredentials(apiKey, apiSecret);
                options.Environment = isTestNet ? BinanceEnvironment.Testnet : BinanceEnvironment.Live;
            });
        }

        // ============================================================
        // 余额
        // ============================================================
        public async Task<decimal> GetBalanceAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Account.GetAccountInfoV3Async();

                if (!result.Success || result.Data == null)
                {
                    throw new Exception(result.Error?.Message ?? "获取账户信息失败");
                }

                var usdt = result.Data.Assets.FirstOrDefault(x => x.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase));

                if (usdt == null)
                {
                    throw new Exception("没有找到 USDT 余额");
                }

                _walletBalance = usdt.WalletBalance;
                OnBalanceUpdated?.Invoke(_walletBalance);

                return _walletBalance;
            }
            catch (Exception ex)
            {
                throw new Exception($"获取余额失败：{ex.Message}", ex);
            }
        }

        // ============================================================
        // 当前价格
        // ============================================================
        public async Task<decimal> GetCurrentPriceAsync(string symbol = "ETHUSDT")
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);

                if (!result.Success || result.Data == null)
                {
                    throw new Exception(result.Error?.Message ?? "获取价格失败");
                }

                _currentPrice = result.Data.LastPrice;
                OnPriceUpdated?.Invoke(_currentPrice);

                return _currentPrice;
            }
            catch (Exception ex)
            {
                throw new Exception($"获取 {symbol} 价格失败：{ex.Message}", ex);
            }
        }

        // ============================================================
        // 设置杠杆
        // ============================================================
        public async Task<bool> SetLeverageAsync(string symbol, int leverage)
        {
            try
            {
                if (leverage < 1)
                    throw new ArgumentOutOfRangeException(nameof(leverage));

                var result = await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage);

                if (!result.Success)
                {
                    throw new Exception(result.Error?.Message ?? "设置杠杆失败");
                }

                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"设置 {symbol} {leverage}X 杠杆失败：{ex.Message}", ex);
            }
        }

        // ============================================================
        // 市价开仓
        // ============================================================
        public async Task<(bool success, long orderId, decimal avgPrice, string errorMessage)> PlaceMarketOrderAsync(
            string symbol,
            OrderSide side,
            decimal quantity,
            int leverage = 20)
        {
            try
            {
                if (quantity <= 0)
                {
                    return (false, 0, 0, "下单数量必须大于 0");
                }

                await SetLeverageAsync(symbol, leverage);

                var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    positionSide: PositionSide.Both);

                if (!result.Success || result.Data == null)
                {
                    return (false, 0, 0, result.Error?.Message ?? "市价下单失败");
                }

                decimal avgPrice = result.Data.AveragePrice;
                if (avgPrice <= 0)
                    avgPrice = _currentPrice;

                return (true, result.Data.Id, avgPrice, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, 0, 0, ex.Message);
            }
        }

        // ============================================================
        // 获取交易步长
        // ============================================================
        public async Task<decimal> GetStepSizeAsync(string symbol = "ETHUSDT")
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

                if (!result.Success || result.Data == null)
                    return 0.001m;

                var sym = result.Data.Symbols.FirstOrDefault(x => x.Name == symbol);

                if (sym?.LotSizeFilter == null)
                    return 0.001m;

                return sym.LotSizeFilter.StepSize;
            }
            catch
            {
                return 0.001m;
            }
        }

        // ============================================================
        // 获取数量规则
        // ============================================================
        public async Task<(decimal StepSize, decimal MinQty, decimal MaxQty)?> GetLotSizeInfoAsync(string symbol = "ETHUSDT")
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

                if (!result.Success || result.Data == null)
                    return null;

                var sym = result.Data.Symbols.FirstOrDefault(x => x.Name == symbol);

                if (sym?.LotSizeFilter == null)
                    return null;

                return (sym.LotSizeFilter.StepSize, sym.LotSizeFilter.MinQuantity, sym.LotSizeFilter.MaxQuantity);
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // 获取交易对
        // ============================================================
        public async Task<List<string>> GetSymbolListAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

                if (!result.Success || result.Data == null)
                {
                    throw new Exception(result.Error?.Message ?? "获取交易对失败");
                }

                return result.Data.Symbols
                    .Where(x => x.Name.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Name)
                    .OrderBy(x => x)
                    .ToList();
            }
            catch (Exception ex)
            {
                throw new Exception($"获取交易对失败：{ex.Message}", ex);
            }
        }

        // ============================================================
        // 止损
        // ============================================================
        public async Task<(bool success, long orderId, string errorMessage)> PlaceStopLossOrderAsync(
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal stopPrice,
            bool reduceOnly = true)
        {
            try
            {
                if (quantity <= 0)
                {
                    return (false, 0, "止损数量必须大于 0");
                }

                if (stopPrice <= 0)
                {
                    return (false, 0, "止损价格必须大于 0");
                }

                var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: FuturesOrderType.StopMarket,
                    quantity: quantity,
                    stopPrice: stopPrice,
                    reduceOnly: reduceOnly,
                    positionSide: PositionSide.Both);

                if (!result.Success || result.Data == null)
                {
                    return (false, 0, result.Error?.Message ?? "止损单创建失败");
                }

                return (true, result.Data.Id, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        // ============================================================
        // 取消订单
        // ============================================================
        public async Task<bool> CancelOrderAsync(string symbol, long orderId)
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId);
                return result.Success;
            }
            catch
            {
                return false;
            }
        }

        // ============================================================
        // 【核心修复处】获取当前持仓
        // ============================================================
        public async Task<List<BinancePositionInfo>> GetPositionsAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Trading.GetPositionsAsync();

                if (!result.Success || result.Data == null)
                {
                    Debug.WriteLine($"[GetPositions] 失败: {result.Error?.Message}");
                    return new List<BinancePositionInfo>();
                }

                var positions = new List<BinancePositionInfo>();

                foreach (var p in result.Data)
                {
                    // 【修复说明】：BinancePositionV3 模型中，表示数量的属性叫 PositionAmt，而不是 Quantity。
                    // 注意：PositionAmt 是带符号的，正数表示做多(Long)，负数表示做空(Short)。
                    if (p.PositionAmt == 0)
                        continue;

                    positions.Add(new BinancePositionInfo
                    {
                        Symbol = p.Symbol,
                        Quantity = p.PositionAmt, // 这里已修复为 PositionAmt
                        EntryPrice = p.EntryPrice,
                        Leverage = p.Leverage ?? 0m
                    });
                }

                return positions;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetPositions] 异常: {ex.Message}");
                return new List<BinancePositionInfo>();
            }
        }

        // ============================================================
        // 获取所有挂单
        // ============================================================
        public async Task<List<BinanceOpenOrderInfo>> GetAllOpenOrdersAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Trading.GetOpenOrdersAsync();

                if (!result.Success || result.Data == null)
                {
                    Debug.WriteLine($"[GetOpenOrders] 失败: {result.Error?.Message}");
                    return new List<BinanceOpenOrderInfo>();
                }

                return result.Data
                    .Select(o => new BinanceOpenOrderInfo
                    {
                        Symbol = o.Symbol,
                        Type = o.Type.ToString(),
                        Side = o.Side.ToString(),
                        StopPrice = o.StopPrice,
                        Quantity = o.Quantity,
                        Id = o.Id
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetOpenOrders] 异常: {ex.Message}");
                return new List<BinanceOpenOrderInfo>();
            }
        }

        // ============================================================
        // Dispose
        // ============================================================
        public void Dispose()
        {
            _restClient.Dispose();
        }
    }

    // ================================================================
    // Binance 内部统一持仓模型 (保持不变，供你的其他代码调用)
    // ================================================================
    public class BinancePositionInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Leverage { get; set; }
    }

    // ================================================================
    // Binance 内部统一挂单模型 (保持不变)
    // ================================================================
    public class BinanceOpenOrderInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public decimal? StopPrice { get; set; }
        public decimal Quantity { get; set; }
        public long Id { get; set; }
    }
}