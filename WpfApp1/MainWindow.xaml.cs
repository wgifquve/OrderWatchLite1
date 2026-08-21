using System;
using System.Collections.Generic;
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
        // ---------- 币安 API 相关 ----------
        private BinanceApiService? _binanceApi;
        private bool _isApiReady = false;

        private string _apiKey = "VStIg7OOM6pDjHUFQwI7SidpGZln1ChXgWlxW5BkAFmk7IgtpoCGzDqmHjdFcyJ2";
        private string _apiSecret = "GN8PWHltiyHAjuCNSV5UgmNT3YN4HkDB7nNWHqA6QB1MeysN1fA3YUN07MjeKmdR";
        private bool _useTestNet = true;

        // ---------- 品种相关 ----------
        private string _selectedSymbol = "ETHUSDT";
        private List<string> _symbolList = new List<string>();

        // ---------- 模拟数据 ----------
        private double _accountBalance = 1000;
        private double _currentPrice = 1200;

        // ---------- 日志 ----------
        private const int MaxLogEntries = 100;
        private List<string> _logEntries = new List<string>();
        private double _selectedRatio = 1.0;
        private double _currentActualMargin = 0;

        public MainWindow()
        {
            InitializeComponent();
            InitializeControls();
            _ = InitializeBinanceApiAsync();
            AddLog("系统启动，正在连接币安...");
            UpdateStatus("连接中...");
        }

        // ---------- 初始化控件 ----------
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

            UpdateAllDisplay();
        }

        // ---------- 初始化币安 API ----------
        private async Task InitializeBinanceApiAsync()
        {
            try
            {
                _binanceApi = new BinanceApiService(_apiKey, _apiSecret, _useTestNet);

                _binanceApi.OnBalanceUpdated += (balance) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _accountBalance = (double)balance;
                        UpdateAllDisplay();
                        AddLog($"💰 余额更新: {balance:F2} USDT");
                    });
                };

                _binanceApi.OnPriceUpdated += (price) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _currentPrice = (double)price;
                        UpdateAllDisplay();
                        lblCurrentPrice.Content = $"{_currentPrice:F2}";
                    });
                };

                await LoadSymbolListAsync();
                await _binanceApi.GetBalanceAsync();
                await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);

                var stepSize = await _binanceApi.GetStepSizeAsync(_selectedSymbol);
                AddLog($"📊 {_selectedSymbol} 步长: {stepSize}");

                _isApiReady = true;
                AddLog($"✅ 币安 API 连接成功 (测试网)，当前品种: {_selectedSymbol}");
                UpdateStatus($"已连接 (测试网) - {_selectedSymbol}");
            }
            catch (Exception ex)
            {
                AddLog($"❌ API 连接失败: {ex.Message}");
                AddLog("⚠️ 将使用模拟数据运行");
                UpdateStatus("离线模式 (模拟)");
                _isApiReady = false;
            }
        }

        // ---------- 加载品种列表 ----------
        private async Task LoadSymbolListAsync()
        {
            if (_binanceApi == null) return;

            try
            {
                var symbolList = await _binanceApi.GetSymbolListAsync();
                Dispatcher.Invoke(() =>
                {
                    cmbSymbol.ItemsSource = symbolList;
                    cmbSymbol.SelectedItem = _selectedSymbol;
                });
                AddLog($"📋 加载了 {symbolList.Count} 个 USDT 交易对");
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 加载品种列表失败: {ex.Message}");
                // 降级方案：手动添加常用品种
                Dispatcher.Invoke(() =>
                {
                    var fallbackList = new List<string>
                    {
                        "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT",
                        "XRPUSDT", "DOGEUSDT", "ADAUSDT", "AVAXUSDT"
                    };
                    cmbSymbol.ItemsSource = fallbackList;
                    cmbSymbol.SelectedItem = _selectedSymbol;
                });
            }
        }

        // ---------- 品种切换事件 ----------
        private async void CmbSymbol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbSymbol.SelectedItem == null) return;
            if (_binanceApi == null) return;

            var newSymbol = cmbSymbol.SelectedItem.ToString();
            if (string.IsNullOrEmpty(newSymbol)) return;

            if (newSymbol != _selectedSymbol)
            {
                _selectedSymbol = newSymbol;
                try
                {
                    await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);
                    var stepSize = await _binanceApi.GetStepSizeAsync(_selectedSymbol);
                    AddLog($"🔄 切换品种至 {_selectedSymbol}，步长: {stepSize}");
                    UpdateStatus($"已连接 (测试网) - {_selectedSymbol}");
                }
                catch (Exception ex)
                {
                    AddLog($"⚠️ 切换品种时获取数据失败: {ex.Message}");
                }
            }
        }

        // ---------- 滑杆事件 ----------
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PositionRatioLabel.Content = $"{PositionRatioSlider.Value:F0}%";
            LeverageLabel.Content = $"{LeverageSlider.Value:F0}X";
            UpdateAllDisplay();
        }

        // ---------- 快速比例按钮 ----------
        private void RatioButton_Checked(object sender, RoutedEventArgs e)
        {
            var current = sender as ToggleButton;
            if (current == null) return;

            ToggleButton[] allButtons = { BtnRatio70, BtnRatio50, BtnRatio30, BtnRatio15 };
            foreach (var btn in allButtons)
            {
                if (btn != current && btn.IsChecked == true)
                    btn.IsChecked = false;
            }

            if (current == BtnRatio70) _selectedRatio = 0.70;
            else if (current == BtnRatio50) _selectedRatio = 0.50;
            else if (current == BtnRatio30) _selectedRatio = 0.30;
            else if (current == BtnRatio15) _selectedRatio = 0.15;

            AddLog($"快速比例设为 {_selectedRatio * 100:F0}%");
            UpdateAllDisplay();
        }

        private void RatioButton_Unchecked(object sender, RoutedEventArgs e)
        {
            bool anyChecked = false;
            ToggleButton[] allButtons = { BtnRatio70, BtnRatio50, BtnRatio30, BtnRatio15 };
            foreach (var btn in allButtons)
            {
                if (btn.IsChecked == true) anyChecked = true;
            }
            if (!anyChecked)
            {
                _selectedRatio = 1.0;
                AddLog("快速比例已清除（100%）");
                UpdateAllDisplay();
            }
        }

        // ---------- 核心计算 ----------
        private void UpdateAllDisplay()
        {
            double baseMargin = _accountBalance * (PositionRatioSlider.Value / 100.0);
            double actualMargin = baseMargin * _selectedRatio;
            double leverage = LeverageSlider.Value;
            double basePositionValue = baseMargin * leverage;
            double actualPositionValue = actualMargin * leverage;

            BaseMarginLabel.Content = $"{baseMargin:F0}U × {leverage:F0}X = {basePositionValue:F0}U";
            ActualMarginLabel.Content = $"{actualMargin:F2} U";
            txtCalculatedValue.Text = actualPositionValue.ToString("F2");

            _currentActualMargin = actualMargin;
        }

        // ---------- 下单 ----------
        private async void BtnBuy_Click(object sender, RoutedEventArgs e) => await ExecuteOrder("BUY", OrderSide.Buy);
        private async void BtnSell_Click(object sender, RoutedEventArgs e) => await ExecuteOrder("SELL", OrderSide.Sell);

        private async Task ExecuteOrder(string sideText, OrderSide side)
        {
            double margin = _currentActualMargin;
            if (margin <= 0)
            {
                AddLog("❌ 实际保证金为0，无法下单");
                UpdateStatus("下单失败：保证金为0");
                return;
            }

            double leverage = LeverageSlider.Value;
            double positionValue = margin * leverage;
            double rawQuantity = positionValue / _currentPrice;

            double quantity;
            if (_isApiReady && _binanceApi != null)
            {
                try
                {
                    var stepSize = await _binanceApi.GetStepSizeAsync(_selectedSymbol);
                    quantity = RoundToStepSize(rawQuantity, (double)stepSize);
                    AddLog($"📐 步长取整: {rawQuantity:F6} → {quantity:F6}");
                }
                catch
                {
                    quantity = Math.Round(rawQuantity, 3);
                }
            }
            else
            {
                quantity = Math.Round(rawQuantity, 3);
            }

            if (quantity <= 0)
            {
                AddLog("❌ 计算出的数量为0，无法下单");
                UpdateStatus("下单失败：数量为0");
                return;
            }

            try
            {
                if (_isApiReady && _binanceApi != null)
                {
                    var orderId = await _binanceApi.PlaceMarketOrderAsync(
                        symbol: _selectedSymbol,
                        side: side,
                        quantity: (decimal)quantity,
                        leverage: (int)leverage
                    );

                    AddLog($"✅ {sideText} {_selectedSymbol} 订单成功 | 订单ID: {orderId} | 保证金={margin:F2}U | 杠杆={leverage:X} | 数量={quantity:F6} | 仓位价值={positionValue:F2}U");
                    UpdateStatus($"✅ {sideText} 单成功 (ID:{orderId})");

                    await _binanceApi.GetBalanceAsync();
                    await _binanceApi.GetCurrentPriceAsync(_selectedSymbol);
                }
                else
                {
                    var rnd = new Random();
                    bool success = rnd.Next(0, 10) > 2;

                    if (success)
                    {
                        AddLog($"✅ {sideText} {_selectedSymbol} 订单成功 (模拟) | 保证金={margin:F2}U | 杠杆={leverage:X} | 数量={quantity:F6} | 仓位价值={positionValue:F2}U");
                        UpdateStatus($"✅ {sideText} 单成功 (模拟)");
                    }
                    else
                    {
                        AddLog($"❌ {sideText} 订单失败 (模拟)");
                        UpdateStatus($"❌ {sideText} 单失败 (模拟)");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ {sideText} 订单异常: {ex.Message}");
                UpdateStatus($"❌ {sideText} 单异常");
            }
        }

        // ---------- 步长取整（MT5风格） ----------
        private double RoundToStepSize(double value, double stepSize)
        {
            if (stepSize <= 0) return Math.Round(value, 3);
            double remainder = value % stepSize;
            if (remainder >= 0.7 * stepSize)
                return Math.Ceiling(value / stepSize) * stepSize;
            else
                return Math.Floor(value / stepSize) * stepSize;
        }

        // ---------- 日志和状态 ----------
        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            _logEntries.Add($"[{time}] {message}");
            if (_logEntries.Count > MaxLogEntries)
                _logEntries.RemoveAt(0);

            lstLog.ItemsSource = null;
            lstLog.ItemsSource = _logEntries;
            lstLog.ScrollIntoView(lstLog.Items[lstLog.Items.Count - 1]);
        }

        private void UpdateStatus(string status)
        {
            txtStatus.Text = status;
        }

        protected override void OnClosed(EventArgs e)
        {
            _binanceApi?.Dispose();
            base.OnClosed(e);
        }
    }
}