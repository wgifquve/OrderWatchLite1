using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrderWatchLite.Services
{
    public class BinanceApiService : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly bool _isTestNet;
        private decimal _walletBalance = 0;
        private decimal _currentPrice = 0;

        public event Action<decimal>? OnBalanceUpdated;
        public event Action<decimal>? OnPriceUpdated;

        public BinanceApiService(string apiKey, string apiSecret, bool isTestNet = true)
        {
            _isTestNet = isTestNet;

            _restClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                options.Environment = isTestNet ? BinanceEnvironment.Testnet : BinanceEnvironment.Live;
            });
        }

        public async Task<decimal> GetBalanceAsync()
        {
            var result = await _restClient.UsdFuturesApi.Account.GetAccountInfoAsync();
            if (result.Success && result.Data != null)
            {
                var usdt = result.Data.Assets.FirstOrDefault(a => a.Asset == "USDT");
                if (usdt != null)
                    _walletBalance = usdt.WalletBalance;
                else
                    _walletBalance = 0m;

                OnBalanceUpdated?.Invoke(_walletBalance);
                return _walletBalance;
            }
            throw new Exception($"获取余额失败: {result.Error?.Message ?? "未知错误"}");
        }

        public async Task<decimal> GetCurrentPriceAsync(string symbol = "ETHUSDT")
        {
            var result = await _restClient.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);
            if (result.Success && result.Data != null)
            {
                _currentPrice = result.Data.LastPrice;
                OnPriceUpdated?.Invoke(_currentPrice);
                return _currentPrice;
            }
            throw new Exception($"获取价格失败: {result.Error?.Message ?? "未知错误"}");
        }

        public async Task<bool> SetLeverageAsync(string symbol = "ETHUSDT", int leverage = 20)
        {
            var result = await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage);
            if (result.Success) return true;
            throw new Exception($"设置杠杆失败: {result.Error?.Message ?? "未知错误"}");
        }

        public async Task<long> PlaceMarketOrderAsync(
            string symbol = "ETHUSDT",
            OrderSide side = OrderSide.Buy,
            decimal quantity = 0,
            int leverage = 20)
        {
            if (quantity <= 0) throw new Exception("下单数量必须大于0");

            await SetLeverageAsync(symbol, leverage);

            var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: FuturesOrderType.Market,
                quantity: quantity,
                positionSide: PositionSide.Both
            );

            if (result.Success && result.Data != null)
                return result.Data.Id;

            throw new Exception($"下单失败: {result.Error?.Message ?? "未知错误"}");
        }

        public async Task<decimal> GetStepSizeAsync(string symbol = "ETHUSDT")
        {
            var info = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            if (info.Success && info.Data != null)
            {
                var sym = info.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (sym?.LotSizeFilter != null)
                    return sym.LotSizeFilter.StepSize;
                else
                    return 0.001m;
            }
            return 0.001m;
        }

        // 直接返回品种列表，避免命名空间问题
        public async Task<List<string>> GetSymbolListAsync()
        {
            var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            if (result.Success && result.Data != null)
            {
                return result.Data.Symbols
                    .Where(s => s.Name.EndsWith("USDT"))
                    .Select(s => s.Name)
                    .OrderBy(s => s)
                    .ToList();
            }
            throw new Exception("获取交易对列表失败");
        }

        public void Dispose() => _restClient?.Dispose();
    }
}