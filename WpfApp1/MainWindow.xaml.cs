using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Binance.Net.Enums;
using OrderWatchLite.Managers;
using OrderWatchLite.Services;

namespace OrderWatchLite
{
    public partial class MainWindow : Window
    {
        private BinanceApiService? _binanceApi;
        private bool _isApiReady = false;
        private string _apiKey = "VStIg7OOM6pDjHUFQwI7SidpGZln1ChXgWlxW5BkAFmk7IgtpoCGzDqmHjdFcyJ2";
        private string _apiSecret = "GN8PWHltiyHAjuCNSV5UgmNT3YN4HkDB7nNWHqA6QB1MeysN1fA3YUN07MjeKmdR";
        private bool _useTestNet = true;

        private string _selectedSymbol = "ETHUSDT";
        private List<string> _symbolList = new List<string>();
        private decimal _accountBalance = 1000m;
        private decimal _currentPrice = 1200m;

        private const int MaxLogEntries = 100;
        private List<string> _logEntries = new List<string>();

        private decimal _selectedRatio = 1.0m;

        private bool _isBreakEvenEnabled = false;
        private decimal _breakEvenPercent = 3m;
        private decimal _stopLossPercent = 5m;

        private PositionManager _positionManager = new PositionManager();
        private System.Windows.Threading.DispatcherTimer? _priceTimer;
        private bool _isSyncing = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeControls();
            _ = InitializeBinanceApiAsync();
            AddLog("系统启动，正在连接币安...");
            UpdateStatus("就绪");
            StartPriceMonitor();
        }

        private void InitializeControls()
        {
            PositionRatioSlider.ValueChanged += Slider_ValueChanged;
            LeverageSlider.ValueChanged += Slider_ValueChanged;

            BtnRatio70.Checked += RatioButton_Checked;
            BtnRatio70.Unchecked += RatioButton_Unchecked;
            BtnRatio50.Checked += RatioButton_Checked;
            BtnRatio50.Unchecked += RatioButton_Unchecked;
            BtnRatio30.Checked += RatioButton_Checked;
            BtnRatio30.Unchecked += RatioButton_Unchecked;
            BtnRatio15.Checked += RatioButton_Checked;
            BtnRatio15.Unchecked += RatioButton_Unchecked;

            BtnBuy.Click += BtnBuy_Click;
            BtnSell.Click += BtnSell_Click;
            cmbSymbol.SelectionChanged += CmbSymbol_SelectionChanged;

            BtnBreakEven.Checked += BtnBreakEven_Checked;
            BtnBreakEven.Unchecked += BtnBreakEven_Unchecked;
            TxtBreakEvenPercent.TextChanged += TxtBreakEvenPercent_TextChanged;

            BtnRefreshOrders.Click += BtnRefreshOrders_Click;

            UpdateAllDisplay();
            UpdateBreakEvenUI();
            UpdateMonitorCount();
        }

        private void UpdateMonitorCount()
        {
            int count = _positionManager.GetActiveBreakEvenCount();
            Dispatcher.Invoke(() =>
            {
                StatusBarMonitorCount.Text = $"🛡️ 监控: {count} 单";
                StatusBarMonitorCount.Foreground = count > 0 ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Gray);
            });
        }

        private void BtnBreakEven_Checked(object sender, RoutedEventArgs e)
        {
            _isBreakEvenEnabled = true;
            BtnBreakEven.Content = "🔓 已开启";
            BtnBreakEven.Background = new SolidColorBrush(Color.FromRgb(129, 199, 132));
            LblBreakEvenStatus.Content = "✅";
            LblBreakEvenStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            StatusBarBreakEven.Text = $"🛡️ 保本已开启 (NL={_breakEvenPercent}%)";
            StatusBarBreakEven.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            AddLog($"🔓 ===== 保本功能已开启，NL={_breakEvenPercent}% =====");
            AddLog($"📌 新订单将自动纳入保本监控");
        }

        private void BtnBreakEven_Unchecked(object sender, RoutedEventArgs e)
        {
            _isBreakEvenEnabled = false;
            BtnBreakEven.Content = "🔒 关闭";
            BtnBreakEven.Background = new SolidColorBrush(Color.FromRgb(232, 232, 232));
            LblBreakEvenStatus.Content = "⏸";
            LblBreakEvenStatus.Foreground = new SolidColorBrush(Colors.Gray);
            StatusBarBreakEven.Text = "🛡️ 保本已暂停";
            StatusBarBreakEven.Foreground = new SolidColorBrush(Colors.Gray);
            AddLog($"🔒 ===== 保本功能已关闭 =====");
        }

        private void TxtBreakEvenPercent_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (decimal.TryParse(TxtBreakEvenPercent.Text, out decimal val))
            {
                if (val >= 0.5m && val <= 50m)
                {
                    _breakEvenPercent = val;
                    if (_isBreakEvenEnabled)
                    {
                        StatusBarBreakEven.Text = $"🛡️ 保本已开启 (NL={_breakEvenPercent}%)";
                        AddLog($"📊 NL% 已更新为 {_breakEvenPercent}%");
                    }
                }
            }
        }

        private void UpdateBreakEvenUI()
        {
            if (_isBreakEvenEnabled)
                BtnBreakEven.IsChecked = true;
            else
                BtnBreakEven.IsChecked = false;
            TxtBreakEvenPercent.Text = _breakEvenPercent.ToString();
        }

        private void StartPriceMonitor()
        {
            _priceTimer = new System.Windows.Threading.DispatcherTimer();
            _priceTimer.Interval = TimeSpan.FromSeconds(1);
            _priceTimer.Tick += async (s, e) => await OnPriceTimerTick();
            _priceTimer.Start();
            AddLog("✅ 价格监控已启动");
        }

        private async Task OnPriceTimerTick()
        {
            if (!_isApiReady || _binanceApi == null) return;

            try
            {
                var price = await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);
                if (price > 0)
                {
                    _currentPrice = price;
                    Dispatcher.Invoke(() => lblCurrentPrice.Content = $"{_currentPrice:F2}");
                }

                if (_isBreakEvenEnabled)
                {
                    int triggered = await _positionManager.CheckBreakEvenAsync(
                        price,
                        _breakEvenPercent,
                        async (oldOrderId, newStopPrice) =>
                        {
                            return await ModifyStopLossOrder(oldOrderId, newStopPrice);
                        }
                    );

                    if (triggered > 0)
                    {
                        AddLog($"✅ 保本触发：{triggered} 个仓位移至开仓价 (NL={_breakEvenPercent}%)");
                        UpdateMonitorCount();
                    }
                }
            }
            catch
            {
                // 静默忽略
            }

            UpdateMonitorCount();
        }

        private async Task<bool> ModifyStopLossOrder(long oldOrderId, decimal newStopPrice)
        {
            if (_binanceApi == null) return false;

            try
            {
                var pos = _positionManager.GetPositionByStopLossOrderId(oldOrderId);
                if (pos == null)
                {
                    AddLog($"❌ 找不到仓位 (OrderId={oldOrderId})");
                    return false;
                }

                var cancelResult = await _binanceApi.CancelOrderAsync(pos.Symbol, oldOrderId);
                if (!cancelResult)
                {
                    AddLog($"❌ 取消旧止损单失败");
                    return false;
                }

                OrderSide stopSide = (pos.Side == OrderSide.Buy) ? OrderSide.Sell : OrderSide.Buy;

                var newOrderResult = await _binanceApi.PlaceStopLossOrderAsync(
                    symbol: pos.Symbol,
                    side: stopSide,
                    quantity: pos.Quantity,
                    stopPrice: newStopPrice,
                    reduceOnly: true
                );

                if (newOrderResult.success && newOrderResult.orderId > 0)
                {
                    _positionManager.UpdateStopLossOrderId(oldOrderId, newOrderResult.orderId);
                    var updatedPos = _positionManager.GetPositionByStopLossOrderId(newOrderResult.orderId);
                    if (updatedPos != null)
                        updatedPos.StopLossPrice = newStopPrice;

                    AddLog($"🔒 保本成功：止损移至开仓价 {newStopPrice:F2}");
                    return true;
                }
                else
                {
                    AddLog($"❌ 新止损单失败：{newOrderResult.errorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ 异常：{ex.Message}");
                return false;
            }
        }

        // ==================== 刷新场内订单 ====================

        private async void BtnRefreshOrders_Click(object sender, RoutedEventArgs e)
        {
            if (_binanceApi == null)
            {
                AddLog("⚠️ API 未就绪");
                return;
            }

            if (_isSyncing)
            {
                AddLog("⏳ 正在同步中，请稍候...");
                return;
            }

            _isSyncing = true;
            BtnRefreshOrders.IsEnabled = false;
            BtnRefreshOrders.Content = "⏳ 同步中...";
            AddLog("🔄 ===== 开始同步场内订单 =====");

            try
            {
                await SyncOrdersFromExchange();
                AddLog("✅ ===== 同步完成 =====");
            }
            catch (Exception ex)
            {
                AddLog($"❌ 同步失败: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
                BtnRefreshOrders.IsEnabled = true;
                BtnRefreshOrders.Content = "🔄 刷新场内订单";
                UpdateMonitorCount();
            }
        }

        /// <summary>
        /// 从币安同步持仓和挂单到本地
        /// 对照 BinanceApiService 13.4 版本：使用 BinancePositionInfo 和 BinanceOpenOrderInfo
        /// </summary>
        private async Task SyncOrdersFromExchange()
        {
            if (_binanceApi == null) return;

            // ===== 1. 获取持仓（现在返回的是 BinancePositionInfo 列表） =====
            var positions = await _binanceApi.GetPositionsAsync();
            AddLog($"📊 获取到 {positions.Count} 个持仓");

            foreach (var pos in positions)
            {
                AddLog($"  持仓: {pos.Symbol}, {(pos.Quantity > 0 ? "多" : "空")}, 数量:{pos.Quantity}, 开仓价:{pos.EntryPrice}");
            }

            // ===== 2. 获取所有挂单（现在返回的是 BinanceOpenOrderInfo 列表） =====
            var openOrders = await _binanceApi.GetAllOpenOrdersAsync();
            AddLog($"📊 获取到 {openOrders.Count} 个挂单");

            foreach (var order in openOrders)
            {
                AddLog($"  挂单: {order.Symbol}, 类型:{order.Type}, 方向:{order.Side}, 止损价:{order.StopPrice}, ID:{order.Id}");
            }

            if (positions.Count == 0 && openOrders.Count == 0)
            {
                var localCount = _positionManager.GetAllPositions().Count;
                if (localCount > 0)
                {
                    _positionManager.Clear();
                    AddLog($"🧹 清理本地残留记录: {localCount} 个");
                }
                AddLog("📭 场内无仓位和挂单");
                return;
            }

            // ===== 3. 遍历持仓，匹配止损单 =====
            int syncedCount = 0;
            foreach (var pos in positions)
            {
                // 过滤出该品种的 StopMarket 挂单
                var stopOrders = openOrders
                    .Where(o => o.Symbol == pos.Symbol && o.Type == "StopMarket")
                    .ToList();

                foreach (var order in stopOrders)
                {
                    // 方向匹配：多单配 Sell 止损，空单配 Buy 止损
                    bool isMatching = false;
                    if (pos.Quantity > 0 && order.Side == "Sell") isMatching = true;
                    if (pos.Quantity < 0 && order.Side == "Buy") isMatching = true;

                    if (isMatching && order.StopPrice != null)
                    {
                        decimal entryPrice = pos.EntryPrice;
                        if (entryPrice <= 0) entryPrice = _currentPrice;

                        decimal stopPrice = order.StopPrice.Value;

                        // 计算 SL%
                        decimal slPercent = 5m;
                        if (pos.Quantity > 0 && entryPrice > 0)
                        {
                            slPercent = (entryPrice - stopPrice) / entryPrice * 100;
                        }
                        else if (pos.Quantity < 0 && entryPrice > 0)
                        {
                            slPercent = (stopPrice - entryPrice) / entryPrice * 100;
                        }
                        if (slPercent < 0) slPercent = -slPercent;
                        if (slPercent < 0.1m) slPercent = 5m;

                        var localPos = new Managers.Position
                        {
                            Symbol = pos.Symbol,
                            Side = pos.Quantity > 0 ? OrderSide.Buy : OrderSide.Sell,
                            Quantity = Math.Abs(pos.Quantity),
                            EntryPrice = entryPrice,
                            Leverage = pos.Leverage,
                            StopLossPrice = stopPrice,
                            StopLossOrderId = order.Id,
                            IsBreakEvenTriggered = false,
                            BreakEvenThreshold = _breakEvenPercent,
                            StopLossPercent = slPercent,
                            OpenTime = DateTime.Now
                        };

                        var existing = _positionManager.GetPositionByStopLossOrderId(order.Id);
                        if (existing == null)
                        {
                            _positionManager.AddPosition(localPos);
                            syncedCount++;
                            AddLog($"✅ 同步: {pos.Symbol} {(pos.Quantity > 0 ? "多" : "空")}单, Qty:{Math.Abs(pos.Quantity):F4}, 止损:{stopPrice:F2}");
                        }
                    }
                }
            }

            if (syncedCount == 0 && positions.Count > 0)
            {
                AddLog($"⚠️ 没有匹配到止损单，请检查：");
                AddLog($"  1. 场内是否有 StopMarket 类型的止损挂单");
                AddLog($"  2. 止损单方向：多单→Sell，空单→Buy");
                AddLog($"  3. 当前获取到 {openOrders.Count} 个挂单，其中 StopMarket 类型有 {openOrders.Count(o => o.Type == "StopMarket")} 个");
            }

            // ===== 4. 如果场内持仓为0，清理本地 =====
            if (positions.Count == 0)
            {
                var allLocal = _positionManager.GetAllPositions();
                if (allLocal.Count > 0)
                {
                    AddLog($"🧹 清理本地残留: {allLocal.Count} 个");
                    _positionManager.Clear();
                }
            }

            // ===== 5. 更新监控计数 =====
            UpdateMonitorCount();
            int activeCount = _positionManager.GetActiveBreakEvenCount();
            if (activeCount > 0 && _isBreakEvenEnabled)
            {
                AddLog($"🛡️ 当前监控 {activeCount} 个订单, NL={_breakEvenPercent}%");
            }
            else if (activeCount > 0 && !_isBreakEvenEnabled)
            {
                AddLog($"⏸ 有 {activeCount} 个订单可监控，但保本未开启");
            }
            else if (activeCount == 0 && positions.Count > 0)
            {
                AddLog($"⚠️ 持仓 {positions.Count} 个，但未匹配到任何止损单，无法纳入监控");
            }
        }

        // ==================== 以下为原有代码 ====================

        private async Task InitializeBinanceApiAsync()
        {
            try
            {
                _binanceApi = new BinanceApiService(_apiKey, _apiSecret, _useTestNet);

                _binanceApi.OnBalanceUpdated += (balance) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _accountBalance = balance;
                        UpdateAllDisplay();
                        AddLog($"💰 余额: {balance:F2} USDT");
                    });
                };

                _binanceApi.OnPriceUpdated += (price) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _currentPrice = price;
                        UpdateAllDisplay();
                        lblCurrentPrice.Content = $"{_currentPrice:F2}";
                    });
                };

                await LoadSymbolListAsync();
                await _binanceApi.GetBalanceAsync();
                await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);

                var stepSize = await _binanceApi.GetStepSizeAsync(_selectedSymbol);
                AddLog($"📐 {_selectedSymbol} 步长: {stepSize}");

                _isApiReady = true;
                AddLog($"✅ 币安连接成功 (测试网)");
                UpdateStatus($"已连接 - {_selectedSymbol}");

                // 连接成功后自动同步一次
                await SyncOrdersFromExchange();
            }
            catch (Exception ex)
            {
                AddLog($"❌ API 失败: {ex.Message}");
                AddLog("⚠️ 模拟模式");
                UpdateStatus("离线模式");
            }
        }

        private async Task LoadSymbolListAsync()
        {
            try
            {
                if (_binanceApi == null) return;
                var symbols = await _binanceApi.GetSymbolListAsync();
                if (symbols != null && symbols.Count > 0)
                {
                    _symbolList = symbols;
                    Dispatcher.Invoke(() =>
                    {
                        cmbSymbol.ItemsSource = _symbolList;
                        if (_symbolList.Contains(_selectedSymbol))
                            cmbSymbol.SelectedItem = _selectedSymbol;
                        else
                            cmbSymbol.SelectedIndex = 0;
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 加载品种失败: {ex.Message}");
            }
        }

        private async void CmbSymbol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbSymbol.SelectedItem == null) return;
            var newSymbol = cmbSymbol.SelectedItem.ToString();
            if (string.IsNullOrEmpty(newSymbol) || newSymbol == _selectedSymbol) return;

            _selectedSymbol = newSymbol;
            AddLog($"🔄 切换: {_selectedSymbol}");

            if (_isApiReady && _binanceApi != null)
            {
                try
                {
                    var price = await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);
                    if (price > 0)
                    {
                        _currentPrice = price;
                        Dispatcher.Invoke(() => lblCurrentPrice.Content = $"{_currentPrice:F2}");
                    }

                    var stepSize = await _binanceApi.GetStepSizeAsync(_selectedSymbol);
                    AddLog($"📐 步长: {stepSize}");
                    UpdateStatus($"已连接 - {_selectedSymbol}");
                }
                catch (Exception ex)
                {
                    AddLog($"⚠️ 获取数据失败: {ex.Message}");
                }
            }

            UpdateAllDisplay();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateAllDisplay();
        }

        private void RatioButton_Checked(object sender, RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;
            if (btn == null) return;

            foreach (var child in ((StackPanel)btn.Parent).Children)
            {
                if (child is ToggleButton other && other != btn && other.IsChecked == true)
                    other.IsChecked = false;
            }

            string content = btn.Content.ToString() ?? "100";
            content = content.Replace("%", "");
            if (double.TryParse(content, out double val))
                _selectedRatio = (decimal)val / 100m;

            UpdateAllDisplay();
        }

        private void RatioButton_Unchecked(object sender, RoutedEventArgs e)
        {
            bool anyChecked = false;
            var btn = sender as ToggleButton;
            if (btn == null) return;

            foreach (var child in ((StackPanel)btn.Parent).Children)
            {
                if (child is ToggleButton other && other.IsChecked == true)
                {
                    anyChecked = true;
                    break;
                }
            }

            if (!anyChecked)
            {
                _selectedRatio = 1.0m;
                UpdateAllDisplay();
            }
        }

        private void UpdateAllDisplay()
        {
            decimal ratio = (decimal)PositionRatioSlider.Value / 100m;
            decimal leverage = (decimal)LeverageSlider.Value;

            decimal baseMargin = _accountBalance * ratio;
            decimal actualMargin = baseMargin * _selectedRatio;
            decimal positionValue = actualMargin * leverage;

            lblRatioValue.Content = $"{PositionRatioSlider.Value:F0}%";
            lblLeverageValue.Content = $"{leverage:F0}X";
            lblTotalValue.Content = $"{positionValue:F2} U";

            txtActualMargin.Text = $"{actualMargin:F2}";
            txtPositionValue.Text = $"{positionValue:F2}";
        }

        private async void BtnBuy_Click(object sender, RoutedEventArgs e)
        {
            await PlaceOrderAsync(OrderSide.Buy);
        }

        private async void BtnSell_Click(object sender, RoutedEventArgs e)
        {
            await PlaceOrderAsync(OrderSide.Sell);
        }

        private async Task PlaceOrderAsync(OrderSide side)
        {
            if (!_isApiReady || _binanceApi == null)
            {
                AddLog("⚠️ API 未就绪");
                return;
            }

            try
            {
                decimal ratio = (decimal)PositionRatioSlider.Value / 100m;
                decimal leverage = (decimal)LeverageSlider.Value;

                decimal baseMargin = _accountBalance * ratio;
                decimal actualMargin = baseMargin * _selectedRatio;
                decimal positionValue = actualMargin * leverage;

                if (positionValue <= 0 || _currentPrice <= 0)
                {
                    AddLog("❌ 仓位价值或价格为0");
                    return;
                }

                decimal rawQuantity = positionValue / _currentPrice;

                var stepSize = await _binanceApi.GetStepSizeAsync(_selectedSymbol);
                decimal minLot = 0.001m;
                decimal maxLot = 10000m;

                var lotInfo = await _binanceApi.GetLotSizeInfoAsync(_selectedSymbol);
                if (lotInfo != null)
                {
                    minLot = lotInfo.Value.MinQty;
                    maxLot = lotInfo.Value.MaxQty;
                    if (stepSize <= 0) stepSize = lotInfo.Value.StepSize;
                }

                if (stepSize <= 0) stepSize = 0.001m;

                decimal roundedQuantity = (decimal)Helpers.LotSizeHelper.RoundLot(
                    (double)rawQuantity,
                    (double)stepSize,
                    (double)minLot,
                    (double)maxLot
                );

                if (roundedQuantity <= 0)
                {
                    AddLog($"❌ 数量为0 (原始: {rawQuantity:F6})");
                    return;
                }

                await _binanceApi.SetLeverageAsync(_selectedSymbol, (int)leverage);

                var orderResult = await _binanceApi.PlaceMarketOrderAsync(
                    symbol: _selectedSymbol,
                    side: side,
                    quantity: roundedQuantity,
                    leverage: (int)leverage
                );

                if (orderResult.success && orderResult.orderId > 0)
                {
                    decimal fillPrice = orderResult.avgPrice > 0 ? orderResult.avgPrice : _currentPrice;
                    if (fillPrice <= 0) fillPrice = _currentPrice;

                    AddLog($"✅ 下单成功！数量: {roundedQuantity:F4}, 成交价: {fillPrice:F2}");

                    decimal stopPrice;
                    if (side == OrderSide.Buy)
                        stopPrice = fillPrice * (1 - _stopLossPercent / 100);
                    else
                        stopPrice = fillPrice * (1 + _stopLossPercent / 100);

                    OrderSide stopSide = (side == OrderSide.Buy) ? OrderSide.Sell : OrderSide.Buy;

                    var stopResult = await _binanceApi.PlaceStopLossOrderAsync(
                        symbol: _selectedSymbol,
                        side: stopSide,
                        quantity: roundedQuantity,
                        stopPrice: stopPrice,
                        reduceOnly: true
                    );

                    if (stopResult.success && stopResult.orderId > 0)
                    {
                        var pos = new Managers.Position
                        {
                            Symbol = _selectedSymbol,
                            Side = side,
                            Quantity = roundedQuantity,
                            EntryPrice = fillPrice,
                            Leverage = leverage,
                            StopLossPrice = stopPrice,
                            StopLossOrderId = stopResult.orderId,
                            IsBreakEvenTriggered = false,
                            BreakEvenThreshold = _breakEvenPercent,
                            StopLossPercent = _stopLossPercent,
                            OpenTime = DateTime.Now
                        };

                        _positionManager.AddPosition(pos);
                        AddLog($"🔒 止损: {stopPrice:F2} (SL={_stopLossPercent}%)");
                        AddLog($"📊 持仓数: {_positionManager.GetPositionCount(_selectedSymbol)}");

                        if (_isBreakEvenEnabled)
                        {
                            AddLog($"🛡️ 该订单已纳入保本监控，浮盈达 {_breakEvenPercent}% 时自动保本");
                        }
                        else
                        {
                            AddLog($"⏸ 该订单未启用保本监控（点击「保本开启」可激活）");
                        }

                        UpdateMonitorCount();
                    }
                    else
                    {
                        AddLog($"⚠️ 止损失败：{stopResult.errorMessage}");
                    }

                    await _binanceApi.GetBalanceAsync();
                }
                else
                {
                    AddLog($"❌ 下单失败：{orderResult.errorMessage}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ 异常：{ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string entry = $"[{timestamp}] {message}";

            Dispatcher.Invoke(() =>
            {
                _logEntries.Add(entry);
                if (_logEntries.Count > MaxLogEntries)
                    _logEntries.RemoveAt(0);

                LogListBox.ItemsSource = null;
                LogListBox.ItemsSource = _logEntries;
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            });
        }

        private void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() => StatusText.Text = $"✅ {status}");
        }

        protected override void OnClosed(EventArgs e)
        {
            _priceTimer?.Stop();
            _positionManager.Clear();
            base.OnClosed(e);
        }
    }
}