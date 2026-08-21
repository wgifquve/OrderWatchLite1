using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Binance.Net.Enums;
using OrderWatchLite.Managers;
using OrderWatchLite.Services;

namespace OrderWatchLite
{
    public partial class MainWindow : Window
    {
        // ============================================================
        // Binance
        // ============================================================

        private readonly bool _useTestNet = true;

        private readonly string _apiKey =
            "VStIg7OOM6pDjHUFQwI7SidpGZln1ChXgWlxW5BkAFmk7IgtpoCGzDqmHjdFcyJ2";

        private readonly string _apiSecret =
            "GN8PWHltiyHAjuCNSV5UgmNT3YN4HkDB7nNWHqA6QB1MeysN1fA3YUN07MjeKmdR";

        private BinanceApiService? _binanceApi;

        // ============================================================
        // 核心：逻辑保护层
        // ============================================================

        private readonly PositionManager _positionManager = new();

        // ============================================================
        // UI / 当前状态
        // ============================================================

        private decimal _accountBalance = 1000m;
        private decimal _currentPrice = 0m;

        private string _selectedSymbol = "BTCUSDT";

        private decimal _selectedQuickRatio = 1m;

        private decimal _breakEvenPercent = 3m;

        private bool _isBreakEvenEnabled = false;

        private decimal _stopLossPercent = 5m;

        private List<PositionInfo> _currentPositions = new();

        private PositionInfo? _selectedPosition = null;

        private readonly ObservableCollection<string> _logEntries = new();

        // 防止多个刷新同时执行
        private bool _isRefreshing;

        // ============================================================
        // 构造
        // ============================================================

        public MainWindow()
        {
            InitializeComponent();

            LogListBox.ItemsSource = _logEntries;

            _binanceApi = new BinanceApiService(
                _apiKey,
                _apiSecret,
                _useTestNet);

            SliderPosition.ValueChanged += SliderPosition_ValueChanged;
            SliderLeverage.ValueChanged += SliderLeverage_ValueChanged;

            TxtBreakEvenPercent.TextChanged += TxtBreakEvenPercent_TextChanged;
            TxtStopLossPercent.TextChanged += TxtStopLossPercent_TextChanged;

            _ = LoadSymbolsAsync();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            timer.Tick += async (s, e) =>
            {
                await UpdatePriceAndBalanceAsync();

                // 保本开启以后，每次价格更新都检查一次
                if (_isBreakEvenEnabled)
                    await CheckBreakEvenAsync();
            };

            timer.Start();

            if (CmbSymbol.Items.Count > 0)
                CmbSymbol.SelectedIndex = 0;

            UpdateUI();

            AddLog("🚀 程序启动，连接测试网...");

            _ = UpdatePriceAndBalanceAsync();

            // 启动后主动扫描一次真实持仓
            _ = RefreshPositionsAsync(false);
        }

        // ============================================================
        // 品种
        // ============================================================

        private async Task LoadSymbolsAsync()
        {
            try
            {
                if (_binanceApi == null)
                    return;

                var symbols = await _binanceApi.GetAllSymbolsAsync();

                CmbSymbol.Items.Clear();

                foreach (var symbol in symbols)
                    CmbSymbol.Items.Add(symbol);

                AddLog($"✅ 加载了 {symbols.Count} 个交易对");
            }
            catch (Exception ex)
            {
                AddLog($"❌ 加载品种失败: {ex.Message}");
            }
        }

        private async void CmbSymbol_DropDownOpened(
            object sender,
            EventArgs e)
        {
            await LoadSymbolsAsync();
        }

        private async void CmbSymbol_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (CmbSymbol.SelectedItem == null)
                return;

            _selectedSymbol =
                CmbSymbol.SelectedItem.ToString()!;

            AddLog($"🔄 切换品种至 {_selectedSymbol}");

            UpdateUI();

            // 切换品种以后立即扫描
            await RefreshPositionsAsync(false);
        }

        // ============================================================
        // 价格 / 余额
        // ============================================================

        private async Task UpdatePriceAndBalanceAsync()
        {
            try
            {
                if (_binanceApi == null)
                    return;

                var price =
                    await _binanceApi.GetCurrentPriceAsync(
                        _selectedSymbol);

                if (price.HasValue)
                {
                    _currentPrice = price.Value;

                    LblCurrentPrice.Content =
                        _currentPrice.ToString("F2");
                }

                var balance =
                    await _binanceApi.GetAccountBalanceAsync();

                if (balance.HasValue)
                    _accountBalance = balance.Value;

                UpdateUI();

                StatusBarConnection.Text =
                    _useTestNet
                        ? "● 已连接 (测试网)"
                        : "● 已连接";

                StatusBarConnection.Foreground =
                    System.Windows.Media.Brushes.Green;
            }
            catch (Exception ex)
            {
                StatusBarConnection.Text = "● 连接失败";

                StatusBarConnection.Foreground =
                    System.Windows.Media.Brushes.Red;

                AddLog($"⚠️ 更新失败: {ex.Message}");
            }
        }

        // ============================================================
        // UI计算
        // ============================================================

        private void UpdateUI()
        {
            decimal ratio =
                (decimal)SliderPosition.Value / 100m;

            LblPositionPercent.Content =
                $"{SliderPosition.Value}%";

            int leverage =
                (int)SliderLeverage.Value;

            LblLeverage.Content =
                $"{leverage}X";

            decimal baseMargin =
                _accountBalance * ratio;

            decimal actualMargin =
                baseMargin * _selectedQuickRatio;

            decimal positionValue =
                actualMargin * leverage;

            LblActualMargin.Content =
                $"{actualMargin:F2} U";

            LblPositionValue.Content =
                $"{positionValue:F2} U";

            LblTotalValue.Content =
                $"{positionValue:F2} U";
        }

        private void SliderPosition_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateUI();
        }

        private void SliderLeverage_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateUI();
        }

        // ============================================================
        // 快速比例
        // ============================================================

        private void QuickRatio_Checked(
            object sender,
            RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;

            if (btn == null)
                return;

            var parent = btn.Parent as Panel;

            if (parent != null)
            {
                foreach (var child in parent.Children)
                {
                    if (child is ToggleButton tb &&
                        tb != btn)
                    {
                        tb.IsChecked = false;
                    }
                }
            }

            string content =
                btn.Content?.ToString()?.TrimEnd('%')
                ?? string.Empty;

            if (decimal.TryParse(content, out decimal val))
            {
                _selectedQuickRatio =
                    val / 100m;

                AddLog($"🔘 快速比例: {content}%");

                UpdateUI();
            }
        }

        private void QuickRatio_Unchecked(
            object sender,
            RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;

            if (btn == null)
                return;

            var parent = btn.Parent as Panel;

            if (parent == null)
                return;

            bool anyChecked = false;

            foreach (var child in parent.Children)
            {
                if (child is ToggleButton tb &&
                    tb.IsChecked == true)
                {
                    anyChecked = true;
                    break;
                }
            }

            if (!anyChecked)
            {
                _selectedQuickRatio = 1m;

                UpdateUI();
            }
        }

        // ============================================================
        // 保本
        // ============================================================

        private void BtnBreakEven_Checked(
            object sender,
            RoutedEventArgs e)
        {
            _isBreakEvenEnabled = true;

            LblBreakEvenStatus.Content =
                "● 已开启";

            LblBreakEvenStatus.Foreground =
                System.Windows.Media.Brushes.Green;

            AddLog(
                $"🔒 保本开启，NL% = {_breakEvenPercent}%");
        }

        private void BtnBreakEven_Unchecked(
            object sender,
            RoutedEventArgs e)
        {
            _isBreakEvenEnabled = false;

            LblBreakEvenStatus.Content =
                "● 已关闭";

            LblBreakEvenStatus.Foreground =
                System.Windows.Media.Brushes.Gray;

            AddLog("🔓 保本关闭");
        }

        private void TxtBreakEvenPercent_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            if (decimal.TryParse(
                TxtBreakEvenPercent.Text,
                out decimal val))
            {
                if (val >= 0)
                    _breakEvenPercent = val;
            }
        }

        private void TxtStopLossPercent_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            if (decimal.TryParse(
                TxtStopLossPercent.Text,
                out decimal val))
            {
                if (val > 0)
                    _stopLossPercent = val;
            }
        }

        // ============================================================
        // BUY / SELL
        // ============================================================

        private async void BtnBuy_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ExecuteOrderAsync(OrderSide.Buy);
        }

        private async void BtnSell_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ExecuteOrderAsync(OrderSide.Sell);
        }

        private async Task ExecuteOrderAsync(
            OrderSide side)
        {
            try
            {
                if (_binanceApi == null)
                {
                    AddLog("❌ API服务未初始化");
                    return;
                }

                // ====================================================
                // 关键：
                // 下单前先扫描 Binance
                // ====================================================

                await RefreshPositionsAsync(false);

                decimal ratio =
                    (decimal)SliderPosition.Value / 100m;

                decimal baseMargin =
                    _accountBalance * ratio;

                decimal actualMargin =
                    baseMargin * _selectedQuickRatio;

                int leverage =
                    (int)SliderLeverage.Value;

                decimal positionValue =
                    actualMargin * leverage;

                if (positionValue <= 0)
                {
                    AddLog("⚠️ 仓位价值为0");
                    return;
                }

                if (_currentPrice <= 0)
                {
                    AddLog("⚠️ 当前价格无效");
                    return;
                }

                var stepInfo =
                    await _binanceApi.GetLotSizeInfoAsync(
                        _selectedSymbol);

                if (stepInfo == null)
                {
                    AddLog("❌ 无法获取步长信息");
                    return;
                }

                decimal rawQuantity =
                    positionValue / _currentPrice;

                decimal qty =
                    RoundToLotSize(
                        rawQuantity,
                        stepInfo.StepSize);

                if (qty <= 0)
                {
                    AddLog("⚠️ 数量过小为0");
                    return;
                }

                decimal stopPrice =
                    side == OrderSide.Buy
                        ? _currentPrice *
                          (1m - _stopLossPercent / 100m)
                        : _currentPrice *
                          (1m + _stopLossPercent / 100m);

                AddLog(
                    $"📤 {side} {qty} {_selectedSymbol}");

                // ====================================================
                // 市价开仓 + 对应保护单
                // ====================================================

                var result =
                    await _binanceApi.PlaceOrderWithStopLossAsync(
                        symbol: _selectedSymbol,
                        side: side,
                        quantity: qty,
                        stopPrice: stopPrice);

                if (!result.success)
                {
                    AddLog(
                        $"❌ 下单失败: {result.error}");

                    return;
                }

                AddLog(
                    $"✅ 下单成功! 主单: {result.orderId}");

                // ====================================================
                // 保护单 ID
                // ====================================================

                long stopOrderId = 0;

                if (!string.IsNullOrWhiteSpace(
                    result.stopOrderId))
                {
                    long.TryParse(
                        result.stopOrderId,
                        out stopOrderId);
                }

                // ====================================================
                // 下单完成以后重新扫描 Binance
                // ====================================================

                await RefreshPositionsAsync(false);

                // ====================================================
                // 找到实际仓位
                // ====================================================

                var actualPosition =
                    _currentPositions.FirstOrDefault(
                        p =>
                            p.Symbol.Equals(
                                _selectedSymbol,
                                StringComparison.OrdinalIgnoreCase)
                            &&
                            p.Side == side);

                decimal actualEntryPrice =
                    actualPosition?.EntryPrice
                    ?? _currentPrice;

                // ====================================================
                // 新增逻辑保护层
                // ====================================================

                if (stopOrderId > 0)
                {
                    var layer = new Position
                    {
                        Symbol = _selectedSymbol,
                        Side = side,
                        Quantity = qty,
                        EntryPrice = actualEntryPrice,
                        Leverage = leverage,
                        StopLossPrice = stopPrice,
                        StopLossOrderId = stopOrderId,
                        IsBreakEvenTriggered = false,
                        BreakEvenThreshold =
                            _breakEvenPercent,
                        StopLossPercent =
                            _stopLossPercent,
                        OpenTime = DateTime.Now
                    };

                    if (_positionManager.AddPosition(layer))
                    {
                        AddLog(
                            $"🛡️ 新增保护层: " +
                            $"{side} {qty} @ {actualEntryPrice:F2}");
                    }
                    else
                    {
                        AddLog(
                            "⚠️ 保护层记录失败");
                    }
                }
                else
                {
                    AddLog(
                        "⚠️ 主单成功，但没有获得保护单ID");
                }

                // 最后再同步一次
                await RefreshPositionsAsync(false);
            }
            catch (Exception ex)
            {
                AddLog(
                    $"❌ 下单异常: {ex.Message}");
            }
        }

        // ============================================================
        // 刷新真实持仓
        // ============================================================

        private async void BtnRefreshOrders_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RefreshPositionsAsync(true);
        }

        /// <summary>
        /// 从 Binance 获取真实持仓。
        ///
        /// 这是整个终端判断仓位的唯一真实来源。
        /// </summary>
        private async Task RefreshPositionsAsync(
            bool showLog)
        {
            if (_isRefreshing)
                return;

            try
            {
                if (_binanceApi == null)
                    return;

                _isRefreshing = true;

                // ====================================================
                // 获取 Binance 实际持仓
                // ====================================================

                var newPositions =
                    await _binanceApi.GetPositionsAsync();

                // 保存旧数据
                var oldPositions =
                    _currentPositions.ToList();

                // ====================================================
                // 先更新UI数据
                // ====================================================

                _currentPositions =
                    newPositions;

                // ====================================================
                // 同步每个真实仓位到逻辑保护层
                // ====================================================

                foreach (var pos in newPositions)
                {
                    var syncResult =
                        _positionManager
                            .SyncWithActualPosition(
                                pos.Symbol,
                                pos.Side,
                                pos.Quantity);

                    // =================================================
                    // 实际仓位增加：
                    // 说明可能是用户在 Binance 手动加仓。
                    //
                    // 这里识别新增数量。
                    // =================================================

                    if (syncResult.AddedQuantity > 0 &&
                        syncResult.PreviousQuantity > 0)
                    {
                        await HandleManualIncreaseAsync(
                            pos,
                            syncResult.AddedQuantity,
                            syncResult.PreviousQuantity);
                    }
                }

                // ====================================================
                // 旧持仓现在已经不存在
                // ====================================================

                foreach (var oldPos in oldPositions)
                {
                    bool stillExists =
                        newPositions.Any(
                            p =>
                                p.Symbol.Equals(
                                    oldPos.Symbol,
                                    StringComparison.OrdinalIgnoreCase)
                                &&
                                p.Side == oldPos.Side
                                &&
                                p.Quantity > 0);

                    if (!stillExists)
                    {
                        _positionManager.SyncWithActualPosition(
                            oldPos.Symbol,
                            oldPos.Side,
                            0);
                    }
                }

                UpdatePositionList();

                UpdateMonitorCount();

                if (showLog)
                {
                    AddLog(
                        $"📊 Binance实际持仓: " +
                        $"{_currentPositions.Count} 个");
                }
            }
            catch (Exception ex)
            {
                AddLog(
                    $"❌ 刷新持仓失败: {ex.Message}");
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        // ============================================================
        // 识别 Binance 手动加仓
        // ============================================================

        private async Task HandleManualIncreaseAsync(
            PositionInfo actualPosition,
            decimal addedQuantity,
            decimal previousQuantity)
        {
            if (_binanceApi == null)
                return;

            if (addedQuantity <= 0)
                return;

            // ========================================================
            // 计算这次新增仓位的大致成交均价
            //
            // Binance 返回的是合并后的持仓均价。
            //
            // 新增成交价 ≈
            //
            // (新总仓位 × 新均价
            //  - 旧总仓位 × 旧均价)
            // / 新增数量
            //
            // ========================================================

            decimal previousEntryPrice =
                GetPreviousEntryPrice(
                    actualPosition.Symbol,
                    actualPosition.Side);

            decimal newEntryPrice =
                actualPosition.EntryPrice;

            decimal addedEntryPrice =
                newEntryPrice;

            if (previousQuantity > 0 &&
                previousEntryPrice > 0 &&
                addedQuantity > 0)
            {
                decimal totalCostNew =
                    actualPosition.Quantity *
                    newEntryPrice;

                decimal totalCostOld =
                    previousQuantity *
                    previousEntryPrice;

                decimal estimatedCost =
                    totalCostNew -
                    totalCostOld;

                decimal estimatedPrice =
                    estimatedCost /
                    addedQuantity;

                if (estimatedPrice > 0)
                    addedEntryPrice = estimatedPrice;
            }

            // ========================================================
            // 如果这个增加已经是终端刚刚记录的层，
            // 就不要重复创建。
            // ========================================================

            decimal trackedQuantity =
                _positionManager.GetTotalQuantity(
                    actualPosition.Symbol,
                    actualPosition.Side);

            decimal difference =
                actualPosition.Quantity -
                trackedQuantity;

            if (difference <= 0)
                return;

            // ========================================================
            // 手动加仓没有新的保护单。
            //
            // 这里需要给这次手动加仓建立保护。
            // ========================================================

            decimal stopPrice =
                actualPosition.Side == OrderSide.Buy
                    ? addedEntryPrice *
                      (1m - _stopLossPercent / 100m)
                    : addedEntryPrice *
                      (1m + _stopLossPercent / 100m);

            AddLog(
                $"📈 检测到手动加仓: " +
                $"{actualPosition.Symbol} " +
                $"{difference}");

            // ========================================================
            // 这里调用 BinanceApiService 创建独立
            // Reduce-Only STOP-MARKET。
            // ========================================================

            var stopResult =
                await _binanceApi.PlaceReduceOnlyStopMarketAsync(
                    symbol: actualPosition.Symbol,
                    side:
                        actualPosition.Side == OrderSide.Buy
                            ? OrderSide.Sell
                            : OrderSide.Buy,
                    quantity: difference,
                    stopPrice: stopPrice);

            if (!stopResult.success)
            {
                AddLog(
                    $"❌ 手动加仓保护单创建失败: " +
                    $"{stopResult.error}");

                return;
            }

            long stopOrderId = 0;

            long.TryParse(
                stopResult.orderId,
                out stopOrderId);

            if (stopOrderId <= 0)
            {
                AddLog(
                    "⚠️ 手动加仓保护单创建成功，但订单ID无效");

                return;
            }

            var layer = new Position
            {
                Symbol = actualPosition.Symbol,
                Side = actualPosition.Side,
                Quantity = difference,
                EntryPrice = addedEntryPrice,
                Leverage = actualPosition.Leverage,
                StopLossPrice = stopPrice,
                StopLossOrderId = stopOrderId,
                IsBreakEvenTriggered = false,
                BreakEvenThreshold =
                    _breakEvenPercent,
                StopLossPercent =
                    _stopLossPercent,
                OpenTime = DateTime.Now
            };

            if (_positionManager.AddPosition(layer))
            {
                AddLog(
                    $"🛡️ 手动加仓已建立保护层: " +
                    $"{difference} @ {addedEntryPrice:F2}");
            }
        }

        // ============================================================
        // 获取之前记录的均价
        // ============================================================

        private decimal GetPreviousEntryPrice(
            string symbol,
            OrderSide side)
        {
            var latest =
                _positionManager
                    .GetLatestPosition(symbol, side);

            if (latest != null &&
                latest.EntryPrice > 0)
            {
                return latest.EntryPrice;
            }

            return 0m;
        }

        // ============================================================
        // 保本检查
        // ============================================================

        private async Task CheckBreakEvenAsync()
        {
            if (!_isBreakEvenEnabled)
                return;

            if (_binanceApi == null)
                return;

            if (_currentPrice <= 0)
                return;

            await _positionManager.CheckBreakEvenAsync(
                _currentPrice,
                _breakEvenPercent,
                async (stopOrderId, breakEvenPrice) =>
                {
                    try
                    {
                        var result =
                            await _binanceApi
                                .ModifyStopLossAsync(
                                    stopOrderId,
                                    breakEvenPrice);

                        if (result.success)
                        {
                            AddLog(
                                $"🔒 保护层 {stopOrderId} " +
                                $"已移动至保本价 " +
                                $"{breakEvenPrice:F2}");
                        }

                        return result.success;
                    }
                    catch (Exception ex)
                    {
                        AddLog(
                            $"❌ 保本修改失败: {ex.Message}");

                        return false;
                    }
                });
        }

        // ============================================================
        // 平仓选中
        // ============================================================

        private async void BtnCloseSelected_Click(
            object sender,
            RoutedEventArgs e)
        {
            // 点平仓以后，第一件事就是重新扫描
            await RefreshPositionsAsync(false);

            if (_selectedPosition == null)
            {
                AddLog(
                    "⚠️ 请先选择要平仓的仓位");

                return;
            }

            // 从最新真实数据重新寻找
            var actual =
                _currentPositions.FirstOrDefault(
                    p =>
                        p.Symbol.Equals(
                            _selectedPosition.Symbol,
                            StringComparison.OrdinalIgnoreCase)
                        &&
                        p.Side == _selectedPosition.Side);

            if (actual == null)
            {
                AddLog(
                    "⚠️ 当前仓位已经不存在");

                return;
            }

            await ClosePositionAsync(actual);
        }

        // ============================================================
        // 全部平仓
        // ============================================================

        private async void BtnCloseAll_Click(
            object sender,
            RoutedEventArgs e)
        {
            // 先扫描
            await RefreshPositionsAsync(false);

            if (_currentPositions.Count == 0)
            {
                AddLog(
                    "⚠️ 没有持仓需要平仓");

                return;
            }

            var result =
                MessageBox.Show(
                    $"确定要一键平仓全部 {_currentPositions.Count} 个持仓吗？",
                    "确认一键平仓",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            if (_binanceApi == null)
                return;

            var toClose =
                _currentPositions.ToList();

            int successCount = 0;
            int failCount = 0;

            foreach (var pos in toClose)
            {
                var closeResult =
                    await _binanceApi.ClosePositionAsync(
                        pos.Symbol,
                        pos.Quantity,
                        pos.Side == OrderSide.Buy
                            ? OrderSide.Sell
                            : OrderSide.Buy);

                if (closeResult.success)
                {
                    successCount++;

                    AddLog(
                        $"✅ 平仓成功: " +
                        $"{pos.Symbol} " +
                        $"{pos.Quantity}");
                }
                else
                {
                    failCount++;

                    AddLog(
                        $"❌ 平仓 {pos.Symbol} 失败: " +
                        $"{closeResult.error}");
                }
            }

            // ========================================================
            // 平仓完成以后重新扫描
            // ========================================================

            await RefreshPositionsAsync(false);

            AddLog(
                $"📊 一键平仓完成: " +
                $"成功 {successCount}，失败 {failCount}");
        }

        // ============================================================
        // 百分比平仓
        // ============================================================

        private async void BtnClosePercent_Click(
            object sender,
            RoutedEventArgs e)
        {
            // 先扫描
            await RefreshPositionsAsync(false);

            if (_currentPositions.Count == 0)
            {
                AddLog(
                    "⚠️ 没有持仓需要平仓");

                return;
            }

            if (!decimal.TryParse(
                TxtClosePercent.Text,
                out decimal percent)
                ||
                percent <= 0
                ||
                percent > 100)
            {
                AddLog(
                    "⚠️ 请输入1-100之间的百分比");

                return;
            }

            var result =
                MessageBox.Show(
                    $"确定要平仓当前持仓的 {percent}% 吗？",
                    "确认百分比平仓",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            if (_binanceApi == null)
                return;

            var toClose =
                _currentPositions.ToList();

            int successCount = 0;
            int failCount = 0;

            foreach (var pos in toClose)
            {
                decimal closeQty =
                    pos.Quantity *
                    (percent / 100m);

                var stepInfo =
                    await _binanceApi
                        .GetLotSizeInfoAsync(
                            pos.Symbol);

                if (stepInfo != null)
                {
                    closeQty =
                        RoundToLotSize(
                            closeQty,
                            stepInfo.StepSize);
                }

                if (closeQty <= 0)
                {
                    AddLog(
                        $"⚠️ {pos.Symbol} 平仓数量过小，跳过");

                    continue;
                }

                var closeResult =
                    await _binanceApi.ClosePositionAsync(
                        pos.Symbol,
                        closeQty,
                        pos.Side == OrderSide.Buy
                            ? OrderSide.Sell
                            : OrderSide.Buy);

                if (closeResult.success)
                {
                    successCount++;

                    AddLog(
                        $"✅ 百分比平仓: " +
                        $"{pos.Symbol} " +
                        $"{closeQty}");
                }
                else
                {
                    failCount++;

                    AddLog(
                        $"❌ 百分比平仓失败: " +
                        $"{pos.Symbol} " +
                        $"{closeResult.error}");
                }
            }

            // ========================================================
            // 平仓完成以后，完全以 Binance 实际数量重新同步
            // ========================================================

            await RefreshPositionsAsync(false);

            AddLog(
                $"📊 百分比平仓完成: " +
                $"成功 {successCount}，失败 {failCount}");
        }

        // ============================================================
        // 平仓单个仓位
        // ============================================================

        private async Task ClosePositionAsync(
            PositionInfo pos)
        {
            if (_binanceApi == null)
                return;

            var confirm =
                MessageBox.Show(
                    $"确定要平仓 {pos.Symbol} 吗？\n" +
                    $"数量: {pos.Quantity:F3}\n" +
                    $"当前盈亏: {pos.UnrealizedPnl:F2} U",
                    "确认平仓",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            // ========================================================
            // 真正执行平仓
            // ========================================================

            OrderSide closeSide =
                pos.Side == OrderSide.Buy
                    ? OrderSide.Sell
                    : OrderSide.Buy;

            var closeResult =
                await _binanceApi.ClosePositionAsync(
                    pos.Symbol,
                    pos.Quantity,
                    closeSide);

            if (!closeResult.success)
            {
                AddLog(
                    $"❌ 平仓失败: " +
                    $"{closeResult.error}");

                return;
            }

            AddLog(
                $"✅ 平仓成功! " +
                $"{pos.Symbol} " +
                $"订单: {closeResult.orderId}");

            // ========================================================
            // 关键：
            // 平仓成功以后重新扫 Binance。
            //
            // PositionManager 根据实际减少数量，
            // 自动从最新层开始扣。
            // ========================================================

            await RefreshPositionsAsync(false);
        }

        // ============================================================
        // 工具：数量步长
        // ============================================================

        private decimal RoundToLotSize(
            decimal quantity,
            decimal stepSize)
        {
            if (stepSize <= 0)
                return quantity;

            decimal steps =
                Math.Floor(
                    quantity / stepSize);

            return steps * stepSize;
        }

        // ============================================================
        // 监控数量
        // ============================================================

        private void UpdateMonitorCount()
        {
            StatusBarMonitorCount.Text =
                $"监控: {_positionManager.GetAllPositions().Count} 层";
        }

        // ============================================================
        // 持仓列表
        // ============================================================

        private void UpdatePositionList()
        {
            PositionListBox.Items.Clear();

            foreach (var pos in _currentPositions)
            {
                string sideText =
                    pos.Side == OrderSide.Buy
                        ? "多"
                        : "空";

                pos.DisplayText =
                    $"{pos.Symbol} | " +
                    $"{sideText} | " +
                    $"数量:{pos.Quantity:F3} | " +
                    $"开仓价:{pos.EntryPrice:F2} | " +
                    $"盈亏:{pos.UnrealizedPnl:F2}U " +
                    $"({pos.PnlPercent:F2}%)";

                PositionListBox.Items.Add(pos);
            }

            if (_selectedPosition != null)
            {
                var selected =
                    _currentPositions.FirstOrDefault(
                        p =>
                            p.Symbol.Equals(
                                _selectedPosition.Symbol,
                                StringComparison.OrdinalIgnoreCase)
                            &&
                            p.Side == _selectedPosition.Side);

                _selectedPosition = selected;
            }
        }

        private void PositionListBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            _selectedPosition =
                PositionListBox.SelectedItem
                as PositionInfo;
        }

        // ============================================================
        // 日志
        // ============================================================

        private void AddLog(string message)
        {
            string time =
                DateTime.Now.ToString("HH:mm:ss");

            _logEntries.Insert(
                0,
                $"[{time}] {message}");

            if (_logEntries.Count > 100)
                _logEntries.RemoveAt(
                    _logEntries.Count - 1);

            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(
                    LogListBox.Items[0]);
            }
        }

        // ============================================================
        // 窗口关闭
        // ============================================================

        protected override void OnClosed(
            EventArgs e)
        {
            try
            {
                _binanceApi?.Dispose();
            }
            catch
            {
            }

            base.OnClosed(e);
        }
    }
}