using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Binance.Net.Enums;
using OrderWatchLite.Services;

namespace OrderWatchLite
{
    public partial class MainWindow : Window
    {
        private readonly bool _useTestNet = true;
        private readonly string _apiKey = "VStIg7OOM6pDjHUFQwI7SidpGZln1ChXgWlxW5BkAFmk7IgtpoCGzDqmHjdFcyJ2";
        private readonly string _apiSecret = "GN8PWHltiyHAjuCNSV5UgmNT3YN4HkDB7nNWHqA6QB1MeysN1fA3YUN07MjeKmdR";

        private BinanceApiService? _binanceApi;

        private decimal _accountBalance = 1000m;
        private decimal _currentPrice = 0m;
        private string _selectedSymbol = "BTCUSDT";
        private decimal _selectedQuickRatio = 1m;
        private decimal _breakEvenPercent = 3m;
        private bool _isBreakEvenEnabled = false;
        private decimal _stopLossPercent = 5m;

        private List<PositionInfo> _currentPositions = new List<PositionInfo>();
        private PositionInfo? _selectedPosition = null;
        private ObservableCollection<string> _logEntries = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();
            LogListBox.ItemsSource = _logEntries;

            _binanceApi = new BinanceApiService(_apiKey, _apiSecret, _useTestNet);

            SliderPosition.ValueChanged += SliderPosition_ValueChanged;
            SliderLeverage.ValueChanged += SliderLeverage_ValueChanged;

            _ = LoadSymbolsAsync();

            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.Tick += async (s, e) => await UpdatePriceAndBalanceAsync();
            timer.Start();

            TxtBreakEvenPercent.TextChanged += (s, e) =>
            {
                if (decimal.TryParse(TxtBreakEvenPercent.Text, out decimal val))
                    _breakEvenPercent = val;
            };
            TxtStopLossPercent.TextChanged += (s, e) =>
            {
                if (decimal.TryParse(TxtStopLossPercent.Text, out decimal val) && val > 0)
                    _stopLossPercent = val;
            };

            if (CmbSymbol.Items.Count > 0)
                CmbSymbol.SelectedIndex = 0;

            UpdateUI();
            AddLog("🚀 程序启动，连接测试网...");
            _ = UpdatePriceAndBalanceAsync();
        }

        private async Task LoadSymbolsAsync()
        {
            try
            {
                if (_binanceApi == null) return;
                var symbols = await _binanceApi.GetAllSymbolsAsync();
                CmbSymbol.Items.Clear();
                foreach (var s in symbols)
                    CmbSymbol.Items.Add(s);
                AddLog($"✅ 加载了 {symbols.Count} 个交易对");
            }
            catch (Exception ex)
            {
                AddLog($"❌ 加载品种失败: {ex.Message}");
            }
        }

        private async void CmbSymbol_DropDownOpened(object sender, EventArgs e) => await LoadSymbolsAsync();

        private void CmbSymbol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSymbol.SelectedItem != null)
            {
                _selectedSymbol = CmbSymbol.SelectedItem.ToString()!;
                AddLog($"🔄 切换品种至 {_selectedSymbol}");
                UpdateUI();
            }
        }

        private async Task UpdatePriceAndBalanceAsync()
        {
            try
            {
                if (_binanceApi == null) return;

                var price = await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);
                if (price.HasValue)
                {
                    _currentPrice = price.Value;
                    LblCurrentPrice.Content = _currentPrice.ToString("F2");
                }

                var balance = await _binanceApi.GetAccountBalanceAsync();
                if (balance.HasValue)
                    _accountBalance = balance.Value;

                UpdateUI();
                StatusBarConnection.Text = "● 已连接 (测试网)";
                StatusBarConnection.Foreground = System.Windows.Media.Brushes.Green;
            }
            catch (Exception ex)
            {
                StatusBarConnection.Text = "● 连接失败";
                StatusBarConnection.Foreground = System.Windows.Media.Brushes.Red;
                AddLog($"⚠️ 更新失败: {ex.Message}");
            }
        }

        private void UpdateUI()
        {
            decimal ratio = (decimal)SliderPosition.Value / 100m;
            LblPositionPercent.Content = $"{SliderPosition.Value}%";

            int leverage = (int)SliderLeverage.Value;
            LblLeverage.Content = $"{leverage}X";

            decimal baseMargin = _accountBalance * ratio;
            decimal actualMargin = baseMargin * _selectedQuickRatio;
            decimal positionValue = actualMargin * leverage;

            LblActualMargin.Content = $"{actualMargin:F2} U";
            LblPositionValue.Content = $"{positionValue:F2} U";
            LblTotalValue.Content = $"{positionValue:F2} U";
        }

        private void SliderPosition_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateUI();
        private void SliderLeverage_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateUI();

        private void QuickRatio_Checked(object sender, RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;
            if (btn == null) return;
            var parent = btn.Parent as Panel;
            if (parent != null)
            {
                foreach (var child in parent.Children)
                {
                    if (child is ToggleButton tb && tb != btn)
                        tb.IsChecked = false;
                }
            }
            string content = btn.Content.ToString().TrimEnd('%');
            if (decimal.TryParse(content, out decimal val))
            {
                _selectedQuickRatio = val / 100m;
                AddLog($"🔘 快速比例: {content}%");
                UpdateUI();
            }
        }

        private void QuickRatio_Unchecked(object sender, RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;
            if (btn == null) return;
            var parent = btn.Parent as Panel;
            if (parent != null)
            {
                bool anyChecked = false;
                foreach (var child in parent.Children)
                {
                    if (child is ToggleButton tb && tb.IsChecked == true)
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
        }

        private void BtnBreakEven_Checked(object sender, RoutedEventArgs e)
        {
            _isBreakEvenEnabled = true;
            LblBreakEvenStatus.Content = "● 已开启";
            LblBreakEvenStatus.Foreground = System.Windows.Media.Brushes.Green;
            AddLog($"🔒 保本开启，NL% = {_breakEvenPercent}%");
        }

        private void BtnBreakEven_Unchecked(object sender, RoutedEventArgs e)
        {
            _isBreakEvenEnabled = false;
            LblBreakEvenStatus.Content = "● 已关闭";
            LblBreakEvenStatus.Foreground = System.Windows.Media.Brushes.Gray;
            AddLog("🔓 保本关闭");
        }

        private async void BtnBuy_Click(object sender, RoutedEventArgs e) => await ExecuteOrderAsync(OrderSide.Buy);
        private async void BtnSell_Click(object sender, RoutedEventArgs e) => await ExecuteOrderAsync(OrderSide.Sell);

        private async Task ExecuteOrderAsync(OrderSide side)
        {
            try
            {
                if (_binanceApi == null) { AddLog("❌ API服务未初始化"); return; }

                decimal ratio = (decimal)SliderPosition.Value / 100m;
                decimal baseMargin = _accountBalance * ratio;
                decimal actualMargin = baseMargin * _selectedQuickRatio;
                int leverage = (int)SliderLeverage.Value;
                decimal positionValue = actualMargin * leverage;

                if (positionValue <= 0) { AddLog("⚠️ 仓位价值为0"); return; }
                if (_currentPrice <= 0) { AddLog("⚠️ 当前价格无效"); return; }

                decimal rawQuantity = positionValue / _currentPrice;
                var stepInfo = await _binanceApi.GetLotSizeInfoAsync(_selectedSymbol);
                if (stepInfo == null) { AddLog("❌ 无法获取步长信息"); return; }

                decimal qty = RoundToLotSize(rawQuantity, stepInfo.StepSize);
                if (qty <= 0) { AddLog("⚠️ 数量过小为0"); return; }

                decimal stopPrice = side == OrderSide.Buy
                    ? _currentPrice * (1 - _stopLossPercent / 100m)
                    : _currentPrice * (1 + _stopLossPercent / 100m);

                var result = await _binanceApi.PlaceOrderWithStopLossAsync(
                    symbol: _selectedSymbol,
                    side: side,
                    quantity: qty,
                    stopPrice: stopPrice
                );

                if (result.success)
                {
                    AddLog($"✅ 下单成功! 主单: {result.orderId}, 止损单: {result.stopOrderId ?? "未设置"}");
                    AddLog($"   止损价: {stopPrice:F2} (SL: {_stopLossPercent}%)");
                    await RefreshPositionsAsync();
                }
                else
                {
                    AddLog($"❌ 下单失败: {result.error}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ 下单异常: {ex.Message}");
            }
        }

        private async void BtnRefreshOrders_Click(object sender, RoutedEventArgs e) => await RefreshPositionsAsync();

        private async Task RefreshPositionsAsync()
        {
            try
            {
                if (_binanceApi == null) return;
                _currentPositions = await _binanceApi.GetPositionsAsync();
                UpdatePositionList();
                AddLog($"📊 获取到 {_currentPositions.Count} 个持仓");
                UpdateMonitorCount();
            }
            catch (Exception ex)
            {
                AddLog($"❌ 刷新持仓失败: {ex.Message}");
            }
        }

        private void UpdatePositionList()
        {
            PositionListBox.Items.Clear();
            foreach (var pos in _currentPositions)
            {
                string sideText = pos.Side == OrderSide.Buy ? "多" : "空";
                pos.DisplayText = $"{pos.Symbol} | {sideText} | 数量:{pos.Quantity:F3} | " +
                                  $"开仓价:{pos.EntryPrice:F2} | 盈亏:{pos.UnrealizedPnl:F2}U ({pos.PnlPercent:F2}%)";
                PositionListBox.Items.Add(pos);
            }
        }

        private void PositionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedPosition = PositionListBox.SelectedItem as PositionInfo;
        }

        private async void BtnCloseSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPosition == null)
            {
                AddLog("⚠️ 请先选择要平仓的仓位");
                return;
            }
            await ClosePositionAsync(_selectedPosition);
        }

        private async void BtnCloseAll_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPositions.Count == 0)
            {
                AddLog("⚠️ 没有持仓需要平仓");
                return;
            }

            var result = MessageBox.Show(
                $"确定要一键平仓全部 {_currentPositions.Count} 个持仓吗？",
                "确认一键平仓",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            if (_binanceApi == null) return;

            int successCount = 0, failCount = 0;
            var toClose = _currentPositions.ToList();
            foreach (var pos in toClose)
            {
                var closeResult = await _binanceApi.ClosePositionAsync(
                    pos.Symbol,
                    pos.Quantity,
                    pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy
                );
                if (closeResult.success)
                {
                    successCount++;
                    _currentPositions.Remove(pos);
                }
                else
                {
                    failCount++;
                    AddLog($"❌ 平仓 {pos.Symbol} 失败: {closeResult.error}");
                }
            }
            UpdatePositionList();
            UpdateMonitorCount();
            AddLog($"📊 一键平仓完成: 成功 {successCount} 个, 失败 {failCount} 个");
        }

        private async void BtnClosePercent_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPositions.Count == 0)
            {
                AddLog("⚠️ 没有持仓需要平仓");
                return;
            }

            if (!decimal.TryParse(TxtClosePercent.Text, out decimal percent) || percent <= 0 || percent > 100)
            {
                AddLog("⚠️ 请输入1-100之间的百分比");
                return;
            }

            var result = MessageBox.Show(
                $"确定要平仓每个持仓的 {percent}% 吗？",
                "确认百分比平仓",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            if (_binanceApi == null) return;

            int successCount = 0, failCount = 0;
            var toClose = _currentPositions.ToList();
            foreach (var pos in toClose)
            {
                decimal closeQty = pos.Quantity * (percent / 100m);
                var stepInfo = await _binanceApi.GetLotSizeInfoAsync(pos.Symbol);
                if (stepInfo != null)
                    closeQty = RoundToLotSize(closeQty, stepInfo.StepSize);

                if (closeQty <= 0)
                {
                    AddLog($"⚠️ {pos.Symbol} 平仓数量过小，跳过");
                    continue;
                }

                var closeResult = await _binanceApi.ClosePositionAsync(
                    pos.Symbol,
                    closeQty,
                    pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy
                );
                if (closeResult.success)
                {
                    successCount++;
                    pos.Quantity -= closeQty;
                    if (pos.Quantity <= 0.0001m)
                        _currentPositions.Remove(pos);
                }
                else
                {
                    failCount++;
                    AddLog($"❌ 百分比平仓 {pos.Symbol} 失败: {closeResult.error}");
                }
            }
            UpdatePositionList();
            UpdateMonitorCount();
            AddLog($"📊 百分比平仓完成: 成功 {successCount} 个, 失败 {failCount} 个");
        }

        private async Task ClosePositionAsync(PositionInfo pos)
        {
            if (_binanceApi == null) return;

            var confirm = MessageBox.Show(
                $"确定要平仓 {pos.Symbol} 吗？\n数量: {pos.Quantity:F3}\n当前盈亏: {pos.UnrealizedPnl:F2} U",
                "确认平仓",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            OrderSide closeSide = pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var closeResult = await _binanceApi.ClosePositionAsync(pos.Symbol, pos.Quantity, closeSide);

            if (closeResult.success)
            {
                AddLog($"✅ 平仓成功! {pos.Symbol} 订单: {closeResult.orderId}");
                _currentPositions.Remove(pos);
                UpdatePositionList();
                UpdateMonitorCount();
            }
            else
            {
                AddLog($"❌ 平仓失败: {closeResult.error}");
            }
        }

        private decimal RoundToLotSize(decimal quantity, decimal stepSize)
        {
            if (stepSize == 0) return quantity;
            decimal steps = Math.Round(quantity / stepSize);
            return steps * stepSize;
        }

        private void UpdateMonitorCount()
        {
            StatusBarMonitorCount.Text = $"监控: {_currentPositions.Count} 单";
        }

        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            _logEntries.Insert(0, $"[{time}] {message}");
            if (_logEntries.Count > 100)
                _logEntries.RemoveAt(_logEntries.Count - 1);
            LogListBox.ScrollIntoView(LogListBox.Items[0]);
        }
    }
}