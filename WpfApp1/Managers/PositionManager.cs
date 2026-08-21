using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Binance.Net.Enums;

namespace OrderWatchLite.Managers
{
    public class PositionManager
    {
        private readonly ConcurrentDictionary<long, Position> _positions = new();

        public void AddPosition(Position pos)
        {
            if (pos == null || pos.StopLossOrderId == 0) return;
            _positions.TryAdd(pos.StopLossOrderId, pos);
        }

        public List<Position> GetAllPositions()
        {
            return _positions.Values.ToList();
        }

        public Position? GetPositionByStopLossOrderId(long orderId)
        {
            _positions.TryGetValue(orderId, out var pos);
            return pos;
        }

        public bool RemovePosition(long stopLossOrderId)
        {
            return _positions.TryRemove(stopLossOrderId, out _);
        }

        public void UpdateStopLossOrderId(long oldOrderId, long newOrderId)
        {
            if (_positions.TryGetValue(oldOrderId, out var pos))
            {
                _positions.TryRemove(oldOrderId, out _);
                pos.StopLossOrderId = newOrderId;
                _positions.TryAdd(newOrderId, pos);
            }
        }

        public void MarkBreakEvenTriggered(long stopLossOrderId)
        {
            if (_positions.TryGetValue(stopLossOrderId, out var pos))
                pos.IsBreakEvenTriggered = true;
        }

        public async Task<int> CheckBreakEvenAsync(
            decimal currentPrice,
            decimal breakEvenPercent,
            Func<long, decimal, Task<bool>> modifyStopLossFunc)
        {
            if (currentPrice <= 0) return 0;

            var positions = GetAllPositions();
            int triggeredCount = 0;

            foreach (var pos in positions)
            {
                if (pos.IsBreakEvenTriggered) continue;
                if (pos.EntryPrice <= 0) continue;

                decimal profitPercent = 0;
                if (pos.Side == OrderSide.Buy)
                    profitPercent = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100;
                else
                    profitPercent = (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100;

                if (profitPercent >= breakEvenPercent)
                {
                    bool success = await modifyStopLossFunc(pos.StopLossOrderId, pos.EntryPrice);
                    if (success)
                    {
                        MarkBreakEvenTriggered(pos.StopLossOrderId);
                        triggeredCount++;
                    }
                }
            }

            return triggeredCount;
        }

        public int GetPositionCount(string symbol)
        {
            return _positions.Values.Count(p => p.Symbol == symbol);
        }

        public int GetActiveBreakEvenCount()
        {
            return _positions.Values.Count(p => !p.IsBreakEvenTriggered);
        }

        public void Clear()
        {
            _positions.Clear();
        }
    }
}