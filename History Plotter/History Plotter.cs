using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo
{
    [Indicator(AccessRights = AccessRights.FullAccess, IsOverlay = true)]
    public class HistoryPlotter : Indicator
    {
        [Parameter("Background Color", DefaultValue = "#FF161A25")]
        public string BackgroundColorHex { get; set; }

        [Parameter("Bullish Candle Color", DefaultValue = "#26A69A")]
        public string BullishColorHex { get; set; }

        [Parameter("Bearish Candle Color", DefaultValue = "#EF5350")]
        public string BearishColorHex { get; set; }

        [Parameter("Show Grid", DefaultValue = false)]
        public bool ShowGrid { get; set; }

        [Parameter("Show Background Image", DefaultValue = false)]
        public bool ShowBackgroundImage { get; set; }
        
        [Parameter("CSV File Path", DefaultValue = "C:\\trades.csv")]
        public string CsvFilePath { get; set; }
        
        [Parameter("Show Labels", DefaultValue = true)]
        public bool ShowLabels { get; set; }
        
        [Parameter("Show SL/TP Levels", DefaultValue = true)]
        public bool ShowSLTP { get; set; }
        
        [Parameter("Buy Color", DefaultValue = "Green")]
        public string BuyColorName { get; set; }
        
        [Parameter("Sell Color", DefaultValue = "Red")]
        public string SellColorName { get; set; }
        
        [Parameter("Date Format", DefaultValue = "yyyy-MM-dd HH:mm:ss")]
        public string DateFormat { get; set; }
        
        [Parameter("Time Offset (hours)", DefaultValue = 0, MinValue = -12, MaxValue = 12)]
        public int TimeOffset { get; set; }
        
        [Parameter("Arrow Buffer (pips)", DefaultValue = 5, MinValue = 0)]
        public double ArrowBuffer { get; set; }
        
        [Parameter("Decimal Separator", DefaultValue = ".")]
        public string DecimalSeparator { get; set; }
        
        [Parameter("Auto Refresh (minutes)", DefaultValue = 5, MinValue = 0)]
        public int RefreshIntervalMinutes { get; set; }
        
        [Parameter("Debug Mode", DefaultValue = false)]
        public bool DebugMode { get; set; }
        
        private List<HistoricalTrade> _trades;
        private Dictionary<string, string> _orderLineIds;
        private Dictionary<string, string> _slLineIds;
        private Dictionary<string, string> _tpLineIds;
        private Dictionary<string, string> _labelIds;
        private Color _buyColor;
        private Color _sellColor;
        private DateTime _lastRefreshTime;
        private FileSystemWatcher _fileWatcher;
        private NumberFormatInfo _numberFormat;

        protected override void Initialize()
        {
            // Set up number format based on specified decimal separator
            _numberFormat = new NumberFormatInfo();
            _numberFormat.NumberDecimalSeparator = DecimalSeparator;
            
            // Set chart background color
            Chart.ColorSettings.BackgroundColor = ColorFromHex(BackgroundColorHex);
            
            // Set candle colors if the properties are available
            try
            {
                var settings = Chart.ColorSettings;
                var type = settings.GetType();
                
                var upProperty = type.GetProperty("UpCandleColor");
                if (upProperty != null)
                    upProperty.SetValue(settings, ColorFromHex(BullishColorHex));
                
                var downProperty = type.GetProperty("DownCandleColor");
                if (downProperty != null)
                    downProperty.SetValue(settings, ColorFromHex(BearishColorHex));
            }
            catch
            {
                Print("Could not set candle colors directly");
            }
            
            // Hide grid if specified by making grid lines transparent
            if (!ShowGrid)
            {
                Chart.ColorSettings.GridLinesColor = Color.FromArgb(0, 0, 0, 0); // Transparent
            }
            
            // Hide background image by drawing a solid color rectangle
            if (!ShowBackgroundImage)
            {
                // Try to disable background image using reflection if available
                try
                {
                    var displaySettings = Chart.GetType().GetProperty("DisplaySettings");
                    if (displaySettings != null)
                    {
                        var settings = displaySettings.GetValue(Chart);
                        var showBackground = settings.GetType().GetProperty("ShowBackgroundImage");
                        if (showBackground != null)
                        {
                            showBackground.SetValue(settings, false);
                        }
                    }
                }
                catch
                {
                    // Fallback: Just log that we couldn't set the property
                    Print("Could not set background image visibility directly");
                }
            }
            
            _trades = new List<HistoricalTrade>();
            _orderLineIds = new Dictionary<string, string>();
            _slLineIds = new Dictionary<string, string>();
            _tpLineIds = new Dictionary<string, string>();
            _labelIds = new Dictionary<string, string>();
            
            _buyColor = Color.FromName(BuyColorName);
            _sellColor = Color.FromName(SellColorName);
            _lastRefreshTime = DateTime.MinValue;
            
            // Load trades from CSV file
            try
            {
                LoadTradesFromCsv();
                PlotTrades();
                
                // Set up file system watcher to detect CSV file changes if file exists
                if (File.Exists(CsvFilePath) && RefreshIntervalMinutes > 0)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(CsvFilePath);
                        string filename = Path.GetFileName(CsvFilePath);
                        
                        _fileWatcher = new FileSystemWatcher(directory, filename);
                        _fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                        _fileWatcher.Changed += FileWatcher_Changed;
                        _fileWatcher.EnableRaisingEvents = true;
                        
                        Print("File watcher set up for: " + CsvFilePath);
                    }
                    catch (Exception ex)
                    {
                        Print("Could not set up file watcher: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Print("Error loading trades: " + ex.Message);
            }
        }
        
        private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            // Throttle the file change events
            if ((DateTime.Now - _lastRefreshTime).TotalSeconds > 5)
            {
                _lastRefreshTime = DateTime.Now;
                RefreshTrades();
            }
        }

        public override void Calculate(int index)
        {
            // Set bar colors for each candle
            if (index > 0)
            {
                if (Bars.ClosePrices[index] > Bars.OpenPrices[index])
                {
                    // Bullish candle
                    Chart.SetBarColor(index, ColorFromHex("#26A69A"));
                }
                else if (Bars.ClosePrices[index] < Bars.OpenPrices[index])
                {
                    // Bearish candle
                    Chart.SetBarColor(index, ColorFromHex("#EF5350"));
                }
            }

            // Check if we need to refresh the trades
            if (RefreshIntervalMinutes > 0 && (DateTime.Now - _lastRefreshTime).TotalMinutes >= RefreshIntervalMinutes)
            {
                RefreshTrades();
            }
        }
        
        private void RefreshTrades()
        {
            _lastRefreshTime = DateTime.Now;
            
            // Clear existing objects
            ClearTradePlots();
            
            // Reload trades
            _trades.Clear();
            LoadTradesFromCsv();
            PlotTrades();
            
            Print($"Refreshed trades at {DateTime.Now}");
        }
        
        private void ClearTradePlots()
        {
            // Remove all chart objects
            foreach (var id in _orderLineIds.Values)
            {
                Chart.RemoveObject(id);
            }
            
            foreach (var id in _slLineIds.Values)
            {
                Chart.RemoveObject(id);
            }
            
            foreach (var id in _tpLineIds.Values)
            {
                Chart.RemoveObject(id);
            }
            
            foreach (var id in _labelIds.Values)
            {
                Chart.RemoveObject(id);
            }
            
            _orderLineIds.Clear();
            _slLineIds.Clear();
            _tpLineIds.Clear();
            _labelIds.Clear();
        }
        
        private void LoadTradesFromCsv()
        {
            if (!File.Exists(CsvFilePath))
            {
                Print("CSV file not found: " + CsvFilePath);
                return;
            }

            string[] lines = File.ReadAllLines(CsvFilePath);
            if (lines.Length < 2)
            {
                Print("CSV file is empty or has no data rows");
                return;
            }

            // Force the date format to match the CSV file
            string dateFormat = "yyyy-MM-dd HH:mm:ss";

            // Skip header line
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (DebugMode)
                {
                    Print($"Processing line {i + 1}: {line}");
                }

                string[] parts = line.Split(',');
                if (parts.Length < 11) // Minimum required columns
                {
                    Print($"Line {i + 1}: Invalid CSV format, not enough columns. Found {parts.Length}, expected at least 11");
                    continue;
                }

                try
                {
                    // Parse date first to validate format
                    DateTime openTime;
                    if (!DateTime.TryParseExact(parts[0].Trim(), dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out openTime))
                    {
                        Print($"Line {i + 1}: Invalid Opening Time format '{parts[0].Trim()}'. Expected format: {dateFormat}");
                        continue;
                    }
                    openTime = openTime.AddHours(TimeOffset);

                    string orderType = parts[1].Trim();
                    string symbol = parts[2].Trim();
                    string setup = parts[3].Trim();

                    double lots;
                    if (!double.TryParse(parts[4].Trim(), NumberStyles.Any, _numberFormat, out lots))
                    {
                        Print($"Line {i + 1}: Invalid Size / Quantity value '{parts[4].Trim()}'");
                        continue;
                    }

                    DateTime? closeTime = null;
                    if (!string.IsNullOrWhiteSpace(parts[5].Trim()))
                    {
                        DateTime parsedCloseTime;
                        if (DateTime.TryParseExact(parts[5].Trim(), dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedCloseTime))
                        {
                            closeTime = parsedCloseTime.AddHours(TimeOffset);
                        }
                        else
                        {
                            Print($"Line {i + 1}: Invalid Closing Time format '{parts[5].Trim()}'");
                        }
                    }

                    double openPrice;
                    if (!double.TryParse(parts[6].Trim(), NumberStyles.Any, _numberFormat, out openPrice))
                    {
                        Print($"Line {i + 1}: Invalid Entry Price value '{parts[6].Trim()}'");
                        continue;
                    }

                    double? closePrice = null;
                    if (!string.IsNullOrWhiteSpace(parts[7].Trim()))
                    {
                        double parsedClosePrice;
                        if (double.TryParse(parts[7].Trim(), NumberStyles.Any, _numberFormat, out parsedClosePrice))
                        {
                            closePrice = parsedClosePrice;
                        }
                        else
                        {
                            Print($"Line {i + 1}: Invalid Closing Price value '{parts[7].Trim()}'");
                        }
                    }

                    double swap = 0;
                    if (!string.IsNullOrWhiteSpace(parts[8].Trim()))
                    {
                        if (!double.TryParse(parts[8].Trim(), NumberStyles.Any, _numberFormat, out swap))
                        {
                            Print($"Line {i + 1}: Invalid Swap value '{parts[8].Trim()}'");
                        }
                    }

                    double commission = 0;
                    if (!string.IsNullOrWhiteSpace(parts[9].Trim()))
                    {
                        if (!double.TryParse(parts[9].Trim(), NumberStyles.Any, _numberFormat, out commission))
                        {
                            Print($"Line {i + 1}: Invalid Commission value '{parts[9].Trim()}'");
                        }
                    }

                    double profit = 0;
                    if (!string.IsNullOrWhiteSpace(parts[10].Trim()))
                    {
                        if (!double.TryParse(parts[10].Trim(), NumberStyles.Any, _numberFormat, out profit))
                        {
                            Print($"Line {i + 1}: Invalid Net Profit value '{parts[10].Trim()}'");
                        }
                    }

                    double? slPrice = null;
                    if (parts.Length > 11 && !string.IsNullOrWhiteSpace(parts[11].Trim()))
                    {
                        double parsedSlPrice;
                        if (double.TryParse(parts[11].Trim(), NumberStyles.Any, _numberFormat, out parsedSlPrice))
                        {
                            slPrice = parsedSlPrice;
                        }
                        else
                        {
                            Print($"Line {i + 1}: Invalid Stop Loss value '{parts[11].Trim()}'");
                        }
                    }

                    double? tpPrice = null;
                    if (parts.Length > 12 && !string.IsNullOrWhiteSpace(parts[12].Trim()))
                    {
                        double parsedTpPrice;
                        if (double.TryParse(parts[12].Trim(), NumberStyles.Any, _numberFormat, out parsedTpPrice))
                        {
                            tpPrice = parsedTpPrice;
                        }
                        else
                        {
                            Print($"Line {i + 1}: Invalid Take Profit value '{parts[12].Trim()}'");
                        }
                    }

                    var trade = new HistoricalTrade
                    {
                        OpenTime = openTime,
                        OrderType = orderType,
                        Lots = lots,
                        Symbol = symbol,
                        OpenPrice = openPrice,
                        SLPrice = slPrice,
                        TPPrice = tpPrice,
                        CloseTime = closeTime,
                        ClosePrice = closePrice,
                        Commission = commission,
                        Swap = swap,
                        Profit = profit,
                        Comment = setup
                    };

                    // Only add trades for the current symbol
                    if (trade.Symbol == SymbolName)
                    {
                        _trades.Add(trade);
                        if (DebugMode)
                        {
                            Print($"Added trade: {trade.Symbol} {trade.OrderType} {trade.Lots} @ {trade.OpenPrice}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print($"Error parsing line {i + 1}: {ex.Message}");
                    if (DebugMode)
                    {
                        Print($"Stack trace: {ex.StackTrace}");
                    }
                }
            }

            Print($"Loaded {_trades.Count} trades for {SymbolName}");
        }
        
        private void PlotTrades()
        {
            foreach (var trade in _trades)
            {
                string tradeId = $"{trade.OpenTime:yyyyMMddHHmmss}_{trade.OrderType}_{trade.Lots}";
                bool isBuy = trade.OrderType.Contains("Buy");
                Color orderColor = isBuy ? _buyColor : _sellColor;
                
                // Plot entry point
                DateTime entryTime = trade.OpenTime;
                double entryPrice = trade.OpenPrice;
                
                // Add arrow at the beginning of the trade
                string arrowId = $"{tradeId}_arrow";
                double arrowPosition = isBuy ? 
                    entryPrice - (ArrowBuffer * Symbol.PipSize) : 
                    entryPrice + (ArrowBuffer * Symbol.PipSize);
                Chart.DrawIcon(arrowId, isBuy ? ChartIconType.UpArrow : ChartIconType.DownArrow, entryTime, arrowPosition, orderColor);
                
                // Plot exit point if available
                if (trade.CloseTime.HasValue && trade.ClosePrice.HasValue)
                {
                    // Create order line
                    Chart.DrawTrendLine(
                        tradeId, 
                        entryTime, entryPrice, 
                        trade.CloseTime.Value, trade.ClosePrice.Value, 
                        orderColor);
                    _orderLineIds.Add(tradeId, tradeId);
                    
                    // Add label if enabled
                    if (ShowLabels)
                    {
                        string labelId = $"{tradeId}_label";
                        string profitText = trade.Profit >= 0 ? "+$" + trade.Profit.ToString("F2") : "-$" + Math.Abs(trade.Profit).ToString("F2");
                        
                        // Calculate pips difference
                        double pipDiff = isBuy ? 
                            (trade.ClosePrice.Value - entryPrice) / Symbol.PipSize :
                            (entryPrice - trade.ClosePrice.Value) / Symbol.PipSize;
                        string pipText = pipDiff >= 0 ? $"+{pipDiff:F1}" : $"-{Math.Abs(pipDiff):F1}";
                        
                        // Calculate middle point for label position
                        DateTime labelTime = entryTime.AddTicks((trade.CloseTime.Value - entryTime).Ticks / 2);
                        double labelPrice = (entryPrice + trade.ClosePrice.Value) / 2;
                        
                        Chart.DrawText(
                            labelId, 
                            $"{trade.OrderType} {trade.Lots} lot\n{profitText}\n{pipText} pips", 
                            labelTime, 
                            labelPrice, 
                            orderColor);
                        _labelIds.Add(tradeId, labelId);
                    }
                }
                else
                {
                    // Trade still open, draw to current time
                    Chart.DrawTrendLine(
                        tradeId, 
                        entryTime, entryPrice, 
                        DateTime.Now, trade.OpenPrice, 
                        orderColor);
                    _orderLineIds.Add(tradeId, tradeId);
                    
                    if (ShowLabels)
                    {
                        string labelId = $"{tradeId}_label";
                        
                        // Calculate pips difference for open trade
                        double pipDiff = isBuy ? 
                            (trade.OpenPrice - entryPrice) / Symbol.PipSize :
                            (entryPrice - trade.OpenPrice) / Symbol.PipSize;
                        string pipText = pipDiff >= 0 ? $"+{pipDiff:F1}" : $"-{Math.Abs(pipDiff):F1}";
                        
                        // Calculate middle point for label position
                        DateTime labelTime = entryTime.AddTicks((DateTime.Now - entryTime).Ticks / 2);
                        double labelPrice = entryPrice;  // For open trades, keep price at entry level
                        
                        Chart.DrawText(
                            labelId, 
                            $"{trade.OrderType} {trade.Lots} lot\n{pipText} pips", 
                            labelTime, 
                            labelPrice, 
                            orderColor);
                        _labelIds.Add(tradeId, labelId);
                    }
                }
                
                // Plot SL line if available
                if (ShowSLTP && trade.SLPrice.HasValue)
                {
                    string slId = $"{tradeId}_sl";
                    DateTime endTime = trade.CloseTime ?? DateTime.Now;
                    var slLine = Chart.DrawTrendLine(
                        slId, 
                        entryTime, trade.SLPrice.Value, 
                        endTime, trade.SLPrice.Value, 
                        Color.FromArgb(128, Color.Red)); // Semi-transparent red
                    slLine.LineStyle = LineStyle.DotsRare;
                    _slLineIds.Add(tradeId, slId);
                }
                
                // Plot TP line if available
                if (ShowSLTP && trade.TPPrice.HasValue)
                {
                    string tpId = $"{tradeId}_tp";
                    DateTime endTime = trade.CloseTime ?? DateTime.Now;
                    var tpLine = Chart.DrawTrendLine(
                        tpId, 
                        entryTime, trade.TPPrice.Value, 
                        endTime, trade.TPPrice.Value, 
                        Color.FromArgb(128, Color.Green)); // Semi-transparent green
                    tpLine.LineStyle = LineStyle.DotsRare;
                    _tpLineIds.Add(tradeId, tpId);
                }
            }
        }

        private Color ColorFromHex(string hex)
        {
            // Remove # if present
            if (hex.StartsWith("#"))
                hex = hex.Substring(1);

            // Parse ARGB values
            if (hex.Length == 8)
            {
                byte a = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte r = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
                return Color.FromArgb(a, r, g, b);
            }
            else
            {
                // Default to full opacity
                byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                return Color.FromArgb(255, r, g, b);
            }
        }
    }
    
    public class HistoricalTrade
    {
        public DateTime OpenTime { get; set; }
        public string OrderType { get; set; }
        public double Lots { get; set; }
        public string Symbol { get; set; }
        public double OpenPrice { get; set; }
        public double? SLPrice { get; set; }
        public double? TPPrice { get; set; }
        public DateTime? CloseTime { get; set; }
        public double? ClosePrice { get; set; }
        public double Commission { get; set; }
        public double Swap { get; set; }
        public double Profit { get; set; }
        public string Comment { get; set; }
    }
}