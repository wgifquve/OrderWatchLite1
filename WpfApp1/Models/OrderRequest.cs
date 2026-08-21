namespace OrderWatchLite.Models
{
    public enum OrderSide { Buy, Sell }
    public enum OrderType { Market, Limit }

    public class OrderRequest
    {
        public string Symbol { get; set; } = "BTCUSDT";
        public OrderSide Side { get; set; }
        public double Quantity { get; set; }
        public double Price { get; set; }
        public OrderType OrderType { get; set; } = OrderType.Market;
    }

    public class OrderResult
    {
        public bool Success { get; set; }
        public string OrderId { get; set; } = "";
        public string Message { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }
}