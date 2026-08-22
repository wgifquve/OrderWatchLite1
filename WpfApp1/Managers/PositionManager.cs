using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrderWatchLite.Managers
{
    /// <summary>
    /// 逻辑保护层管理器。
    ///
    /// Binance 实际上只有一个合并仓位，
    /// 本类负责在程序内部记录每次下单形成的保护层。
    /// </summary>
    public class PositionManager
    {
        private readonly ConcurrentDictionary<long, Position> _positions = new();

        // ============================================================
        // 新增保护层
        // ============================================================

        public bool AddPosition(Position pos)
        {
            if (pos == null)
                return false;

            if (pos.StopLossOrderId <= 0)
                return false;

            if (string.IsNullOrWhiteSpace(pos.Symbol))
                return false;

            if (pos.Quantity <= 0)
                return false;

            return _positions.TryAdd(
                pos.StopLossOrderId,
                pos);
        }

        public bool AddPositionWithTemporaryId(
            Position pos,
            long temporaryId)
        {
            if (pos == null)
                return false;

            if (temporaryId == 0)
                return false;

            if (pos.Quantity <= 0)
                return false;

            if (string.IsNullOrWhiteSpace(pos.Symbol))
                return false;

            pos.StopLossOrderId = temporaryId;

            return _positions.TryAdd(
                temporaryId,
                pos);
        }

        // ============================================================
        // 查询
        // ============================================================

        public List<Position> GetAllPositions()
        {
            return _positions.Values
                .OrderBy(p => p.OpenTime)
                .ToList();
        }

        public List<Position> GetPositions(
            string symbol,
            OrderSide side)
        {
            return _positions.Values
                .Where(p =>
                    p.Symbol.Equals(
                        symbol,
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    p.Side == side)
                .OrderBy(p => p.OpenTime)
                .ToList();
        }

        public Position? GetPositionByStopLossOrderId(
            long orderId)
        {
            _positions.TryGetValue(
                orderId,
                out var pos);

            return pos;
        }

        public int GetPositionCount(
            string symbol)
        {
            return _positions.Values.Count(p =>
                p.Symbol.Equals(
                    symbol,
                    StringComparison.OrdinalIgnoreCase));
        }

        public int GetActiveBreakEvenCount()
        {
            return _positions.Values.Count(
                p => p.IsBreakEvenTriggered);
        }

        // ============================================================
        // 删除保护层
        // ============================================================

        public bool RemovePosition(
            long stopLossOrderId)
        {
            return _positions.TryRemove(
                stopLossOrderId,
                out _);
        }

        public void RemovePosition(
            Position position)
        {
            if (position == null)
                return;

            if (position.StopLossOrderId != 0)
            {
                _positions.TryRemove(
                    position.StopLossOrderId,
                    out _);
            }
        }

        // ============================================================
        // 修改保护订单 ID
        // ============================================================

        public bool UpdateStopLossOrderId(
            long oldOrderId,
            long newOrderId)
        {
            if (oldOrderId <= 0 ||
                newOrderId <= 0)
                return false;

            if (oldOrderId == newOrderId)
                return true;

            if (!_positions.TryGetValue(
                oldOrderId,
                out var pos))
            {
                return false;
            }

            if (_positions.ContainsKey(newOrderId))
                return false;

            if (!_positions.TryRemove(
                oldOrderId,
                out pos))
            {
                return false;
            }

            pos.StopLossOrderId =
                newOrderId;

            return _positions.TryAdd(
                newOrderId,
                pos);
        }

        // ============================================================
        // 保本状态
        // ============================================================

        public bool MarkBreakEvenTriggered(
            long stopLossOrderId)
        {
            if (!_positions.TryGetValue(
                stopLossOrderId,
                out var pos))
            {
                return false;
            }

            pos.IsBreakEvenTriggered = true;
            pos.StopLossPrice = pos.EntryPrice;

            return true;
        }

        public bool IsBreakEvenTriggered(
            long stopLossOrderId)
        {
            return _positions.TryGetValue(
                stopLossOrderId,
                out var pos)
                &&
                pos.IsBreakEvenTriggered;
        }

        // ============================================================
        // 更新数量
        // ============================================================

        public bool UpdateQuantity(
            long stopLossOrderId,
            decimal quantity)
        {
            if (!_positions.TryGetValue(
                stopLossOrderId,
                out var pos))
            {
                return false;
            }

            if (quantity <= 0)
            {
                _positions.TryRemove(
                    stopLossOrderId,
                    out _);

                return true;
            }

            pos.Quantity = quantity;

            return true;
        }

        // ============================================================
        // 根据 Binance 实际持仓同步
        // ============================================================

        public SyncResult SyncWithActualPosition(
            string symbol,
            OrderSide side,
            decimal actualQuantity)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return SyncResult.Failed("品种为空");

            if (actualQuantity < 0)
            {
                return SyncResult.Failed(
                    "实际持仓数量不能小于 0");
            }

            var layers =
                GetPositions(symbol, side);

            decimal recordedQuantity =
                layers.Sum(p => p.Quantity);

            // ========================================================
            // 实际仓位归零
            // ========================================================

            if (actualQuantity <= 0)
            {
                int removedCount = 0;

                foreach (var layer in layers)
                {
                    if (_positions.TryRemove(
                        layer.StopLossOrderId,
                        out _))
                    {
                        removedCount++;

                        // 记录需要真正从 Binance 取消的保护单
                        // 如果 ID > 0，MainWindow 会调用
                        // BinanceApiService.CancelStopLossAsync()
                        SyncResult temp =
                            new SyncResult();

                        // 不在这里执行网络操作。
                        // 下面统一通过 Result 记录。
                    }
                }

                return new SyncResult
                {
                    Success = true,
                    ActualQuantity = 0,
                    PreviousQuantity = recordedQuantity,
                    ReducedQuantity = recordedQuantity,
                    AddedQuantity = 0,
                    RemovedLayerCount = removedCount,
                    RemovedProtectionOrderIds =
                        layers
                            .Where(p =>
                                p.StopLossOrderId > 0)
                            .Select(p =>
                                p.StopLossOrderId)
                            .ToList(),
                    Message =
                        $"实际仓位已归零，清除 {removedCount} 个保护层"
                };
            }

            // ========================================================
            // 没有逻辑层
            // ========================================================

            if (layers.Count == 0)
            {
                return new SyncResult
                {
                    Success = true,
                    ActualQuantity = actualQuantity,
                    PreviousQuantity = 0,
                    ReducedQuantity = 0,
                    AddedQuantity = actualQuantity,
                    Message =
                        "当前没有逻辑保护层，等待新的下单记录"
                };
            }

            // ========================================================
            // 实际仓位没有减少
            // ========================================================

            if (actualQuantity >= recordedQuantity)
            {
                return new SyncResult
                {
                    Success = true,
                    ActualQuantity = actualQuantity,
                    PreviousQuantity = recordedQuantity,
                    ReducedQuantity = 0,
                    AddedQuantity =
                        actualQuantity - recordedQuantity,
                    Message =
                        actualQuantity > recordedQuantity
                            ? $"实际仓位增加 {actualQuantity - recordedQuantity}"
                            : "实际仓位没有变化"
                };
            }

            // ========================================================
            // 实际仓位减少
            // ========================================================

            decimal reduceQuantity =
                recordedQuantity - actualQuantity;

            decimal remainingReduction =
                reduceQuantity;

            int modifiedLayers = 0;
            int removedLayers = 0;

            var removedIds =
                new List<long>();

            var reducedLayers =
                new List<LayerQuantityChange>();

            // 最新的一层优先减少
            foreach (var layer in layers
                .OrderByDescending(p => p.OpenTime)
                .ThenByDescending(p => p.StopLossOrderId))
            {
                if (remainingReduction <= 0)
                    break;

                decimal layerQuantity =
                    layer.Quantity;

                if (layerQuantity <= 0)
                    continue;

                // ====================================================
                // 整层被减掉
                // ====================================================

                if (remainingReduction >= layerQuantity)
                {
                    remainingReduction -=
                        layerQuantity;

                    if (_positions.TryRemove(
                        layer.StopLossOrderId,
                        out _))
                    {
                        removedLayers++;
                        modifiedLayers++;

                        if (layer.StopLossOrderId > 0)
                        {
                            removedIds.Add(
                                layer.StopLossOrderId);
                        }
                    }
                }
                else
                {
                    decimal newQuantity =
                        layerQuantity -
                        remainingReduction;

                    decimal actualReduced =
                        layerQuantity -
                        newQuantity;

                    layer.Quantity =
                        newQuantity;

                    reducedLayers.Add(
                        new LayerQuantityChange
                        {
                            StopLossOrderId =
                                layer.StopLossOrderId,

                            OldQuantity =
                                layerQuantity,

                            NewQuantity =
                                newQuantity,

                            ReducedQuantity =
                                actualReduced
                        });

                    remainingReduction = 0;
                    modifiedLayers++;
                }
            }

            decimal actualLayerQuantity =
                _positions.Values
                    .Where(p =>
                        p.Symbol.Equals(
                            symbol,
                            StringComparison.OrdinalIgnoreCase)
                        &&
                        p.Side == side)
                    .Sum(p => p.Quantity);

            return new SyncResult
            {
                Success = true,
                ActualQuantity = actualQuantity,
                PreviousQuantity = recordedQuantity,
                ReducedQuantity = reduceQuantity,
                AddedQuantity = 0,
                RemainingLayerQuantity =
                    actualLayerQuantity,
                ModifiedLayerCount =
                    modifiedLayers,
                RemovedLayerCount =
                    removedLayers,
                RemovedProtectionOrderIds =
                    removedIds,
                ReducedProtectionLayers =
                    reducedLayers,
                Message =
                    $"实际减仓 {reduceQuantity}，从最新保护层开始调整，" +
                    $"删除 {removedLayers} 层，修改 {modifiedLayers} 层"
            };
        }

        // ============================================================
        // 根据实际 Binance 仓位同步全部方向
        // ============================================================

        public List<SyncResult> SyncAllActualPositions(
            IEnumerable<ActualPositionSnapshot> actualPositions)
        {
            var results =
                new List<SyncResult>();

            if (actualPositions == null)
                return results;

            var snapshots =
                actualPositions.ToList();

            var trackedGroups =
                _positions.Values
                    .Select(p => new
                    {
                        p.Symbol,
                        p.Side
                    })
                    .Distinct()
                    .ToList();

            foreach (var snapshot in snapshots)
            {
                results.Add(
                    SyncWithActualPosition(
                        snapshot.Symbol,
                        snapshot.Side,
                        snapshot.Quantity));
            }

            foreach (var group in trackedGroups)
            {
                bool exists =
                    snapshots.Any(p =>
                        p.Symbol.Equals(
                            group.Symbol,
                            StringComparison.OrdinalIgnoreCase)
                        &&
                        p.Side == group.Side
                        &&
                        p.Quantity > 0);

                if (!exists)
                {
                    results.Add(
                        SyncWithActualPosition(
                            group.Symbol,
                            group.Side,
                            0));
                }
            }

            return results;
        }

        // ============================================================
        // 保本检查
        // ============================================================

        public async Task<int> CheckBreakEvenAsync(
            decimal currentPrice,
            decimal breakEvenPercent,
            Func<
                long,
                decimal,
                Task<(
                    bool success,
                    long newOrderId,
                    string error)>>
                modifyStopLossFunc)
        {
            if (currentPrice <= 0)
                return 0;

            if (breakEvenPercent < 0)
                return 0;

            if (modifyStopLossFunc == null)
                return 0;

            var positions =
                GetAllPositions();

            int triggeredCount = 0;

            foreach (var pos in positions)
            {
                if (pos.IsBreakEvenTriggered)
                    continue;

                if (pos.EntryPrice <= 0)
                    continue;

                decimal profitPercent;

                if (pos.Side == OrderSide.Buy)
                {
                    profitPercent =
                        (currentPrice - pos.EntryPrice)
                        / pos.EntryPrice
                        * 100m;
                }
                else
                {
                    profitPercent =
                        (pos.EntryPrice - currentPrice)
                        / pos.EntryPrice
                        * 100m;
                }

                if (profitPercent < breakEvenPercent)
                    continue;

                bool success = false;
                long newOrderId = 0;

                try
                {
                    var result =
                        await modifyStopLossFunc(
                            pos.StopLossOrderId,
                            pos.EntryPrice);

                    success = result.success;
                    newOrderId = result.newOrderId;
                }
                catch
                {
                    success = false;
                }

                if (!success)
                    continue;

                if (newOrderId <= 0)
                    continue;

                long oldOrderId =
                    pos.StopLossOrderId;

                if (oldOrderId != newOrderId)
                {
                    if (!UpdateStopLossOrderId(
                        oldOrderId,
                        newOrderId))
                    {
                        continue;
                    }
                }

                if (MarkBreakEvenTriggered(
                    newOrderId))
                {
                    triggeredCount++;
                }
            }

            return triggeredCount;
        }

        // ============================================================
        // 清空
        // ============================================================

        public void Clear()
        {
            _positions.Clear();
        }

        public void ClearSymbol(
            string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            foreach (var pos in _positions.Values)
            {
                if (pos.Symbol.Equals(
                    symbol,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _positions.TryRemove(
                        pos.StopLossOrderId,
                        out _);
                }
            }
        }

        // ============================================================
        // 调试 / 统计
        // ============================================================

        public decimal GetTotalQuantity(
            string symbol,
            OrderSide side)
        {
            return _positions.Values
                .Where(p =>
                    p.Symbol.Equals(
                        symbol,
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    p.Side == side)
                .Sum(p => p.Quantity);
        }

        public Position? GetLatestPosition(
            string symbol,
            OrderSide side)
        {
            return _positions.Values
                .Where(p =>
                    p.Symbol.Equals(
                        symbol,
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    p.Side == side)
                .OrderByDescending(
                    p => p.OpenTime)
                .ThenByDescending(
                    p => p.StopLossOrderId)
                .FirstOrDefault();
        }

        public List<Position> GetLatestFirst(
            string symbol,
            OrderSide side)
        {
            return _positions.Values
                .Where(p =>
                    p.Symbol.Equals(
                        symbol,
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    p.Side == side)
                .OrderByDescending(
                    p => p.OpenTime)
                .ThenByDescending(
                    p => p.StopLossOrderId)
                .ToList();
        }
    }

    // ================================================================
    // Binance 实际持仓快照
    // ================================================================

    public class ActualPositionSnapshot
    {
        public string Symbol { get; set; } =
            string.Empty;

        public OrderSide Side { get; set; }

        public decimal Quantity { get; set; }
    }

    // ================================================================
    // 保护层数量变化
    // ================================================================

    public class LayerQuantityChange
    {
        public long StopLossOrderId { get; set; }

        public decimal OldQuantity { get; set; }

        public decimal NewQuantity { get; set; }

        public decimal ReducedQuantity { get; set; }
    }

    // ================================================================
    // 同步结果
    // ================================================================

    public class SyncResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } =
            string.Empty;

        public string Error { get; set; } =
            string.Empty;

        public decimal ActualQuantity { get; set; }

        public decimal PreviousQuantity { get; set; }

        public decimal ReducedQuantity { get; set; }

        public decimal AddedQuantity { get; set; }

        public decimal RemainingLayerQuantity { get; set; }

        public int ModifiedLayerCount { get; set; }

        public int RemovedLayerCount { get; set; }

        // ============================================================
        // 本次实际需要从 Binance 取消的保护单
        // ============================================================

        public List<long> RemovedProtectionOrderIds { get; set; } =
            new();

        // ============================================================
        // 本次实际需要缩量重建的保护单
        // ============================================================

        public List<LayerQuantityChange> ReducedProtectionLayers { get; set; } =
            new();

        public static SyncResult Failed(
            string error)
        {
            return new SyncResult
            {
                Success = false,
                Error = error,
                Message = error
            };
        }
    }
}