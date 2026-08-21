using System;
using System.Threading.Tasks;
using OrderWatchLite.Models;

namespace OrderWatchLite.Services
{
    public class BinanceService
    {
        public async Task<OrderResult> PlaceOrderAsync(OrderRequest request)
        {
            await Task.Delay(300);

            var rnd = new Random();
            bool success = rnd.Next(0, 10) > 2;

            if (success)
            {
                return new OrderResult
                {
                    Success = true,
                    OrderId = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Message = $"模拟下单成功：{request.Side} {request.Quantity} {request.Symbol}"
                };
            }
            else
            {
                return new OrderResult
                {
                    Success = false,
                    ErrorMessage = "模拟网络错误，请重试"
                };
            }
        }
    }
}