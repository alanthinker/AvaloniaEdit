// 显示 IME 组合(拼音)文本的图层.
// 在富文本编辑器里, 普通的 Avalonia TextBox 通过 TextPresenter 的 PreeditText
// 在输入框内原地上显示带下划线的拼音, 且光标跟随到拼音末尾. 但 AvaloniaEdit 的
// TextArea 之前 SupportsPreedit == false, 且 SetPreeditText 是空实现, 导致 Windows
// 的 IMM32 组合字符串 (例如微信输入法输入的 "ni'hao") 根本没有被绘制出来.
// 本图层复现 TextBox 的行为: 在光标处原地上绘制带下划线的组合文本, 并把组合光标
// 画在组合文本末尾 (或 IME 提供的光标位置), 让光标跟随到拼音末尾.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Utils;

namespace AvaloniaEdit.Editing
{
    /// <summary>
    /// 在光标位置显示 IME 组合 (preedit/拼音) 文本的图层: 带下划线, 并在组合文本
    /// 末尾画一个闪烁光标, 用于让富文本输入框 (te:TextEditor) 里也能像普通文本框
    /// 一样看到拼音, 且光标跟随到拼音末尾.
    /// </summary>
    internal sealed class PreeditLayer : Layer
    {
        private readonly TextView _textView;
        private string _preeditText = string.Empty;

        /// <summary>
        /// 光标在组合文本内的偏移; null 表示放在组合文本末尾 (拼音输入法的常见情况).
        /// </summary>
        private int? _cursorPos;

        /// <summary>
        /// 组合文本的起点, 使用文档坐标 (即文档光标矩形左上角, 相对文档顶部).
        /// </summary>
        private Point _startDocumentPos;

        private readonly DispatcherTimer _caretBlinkTimer = new DispatcherTimer();
        private bool _blink;
        private bool _isComposing;

        public PreeditLayer(TextView textView) : base(textView, KnownLayer.Caret)
        {
            _textView = textView;
            _caretBlinkTimer.Tick += CaretBlinkTimer_Tick;
        }

        private void CaretBlinkTimer_Tick(object sender, EventArgs e)
        {
            _blink = !_blink;
            InvalidateVisual();
        }

        /// <summary>
        /// 显示组合文本. <paramref name="preeditText"/> 为空表示清除.
        /// </summary>
        public void SetPreedit(string preeditText, Point startDocumentPos, int? cursorPos)
        {
            if (string.IsNullOrEmpty(preeditText))
            {
                Clear();
                return;
            }

            _preeditText = preeditText;
            _startDocumentPos = startDocumentPos;
            _cursorPos = cursorPos;

            if (!_isComposing)
            {
                _isComposing = true;
                _blink = true; // 组合光标初始应可见
                _caretBlinkTimer.Interval = TimeSpan.FromMilliseconds(500);
                _caretBlinkTimer.Start();
            }

            InvalidateVisual();
        }

        /// <summary>
        /// 清除组合文本 (组合结束/提交/失焦时调用), 并停止组合光标闪烁.
        /// </summary>
        public void Clear()
        {
            if (_isComposing)
            {
                _isComposing = false;
                _caretBlinkTimer.Stop();
            }

            if (_preeditText.Length != 0 || _cursorPos != null)
            {
                _preeditText = string.Empty;
                _cursorPos = null;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext drawingContext)
        {
            base.Render(drawingContext);

            if (_preeditText.Length == 0 || _textView?.Document == null)
                return;

            // 与正文文本使用相同的字体与颜色 (TextView 的字体/前景继承自 TextEditor).
            var brush = _textView.GetValue(TextElement.ForegroundProperty);
            if (brush == null)
                brush = Brushes.Black;

            var typeface = _textView.CreateTypeface();
            var formattedText = new FormattedText(
                _preeditText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                _textView.FontSize,
                brush);

            // 文档坐标 -> 图层可见区域坐标 (和 CaretLayer 定位光标完全一致).
            var x = _startDocumentPos.X - _textView.HorizontalOffset;
            var y = _startDocumentPos.Y - _textView.VerticalOffset;

            // 起点在可见区域之外就不绘制 (例如光标所在行已经滚出视口).
            var viewport = _textView.Bounds.Size;
            if (x > viewport.Width || y > viewport.Height || y + formattedText.Height < 0)
                return;

            drawingContext.DrawText(formattedText, new Point(x, y));

            // 下划线, 模拟“正在输入拼音”的组合态.
            var underlinePen = new Pen(brush, 1);
            var underlineY = y + formattedText.Height - 2;
            drawingContext.DrawLine(underlinePen, new Point(x, underlineY), new Point(x + formattedText.Width, underlineY));

            // 组合光标: 默认在组合文本末尾 (拼音输入法); 若 IME 提供了光标位置则用之.
            var caretX = GetCaretX(x, typeface, brush, formattedText);

            if (_blink)
            {
                var caretPen = new Pen(brush, 1);
                drawingContext.DrawLine(caretPen, new Point(caretX, y), new Point(caretX, y + formattedText.Height));
            }
        }

        private double GetCaretX(double x, Typeface typeface, IBrush brush, FormattedText fullText)
        {
            if (_cursorPos is int cp && cp >= 0 && cp < _preeditText.Length)
            {
                var prefix = new FormattedText(
                    _preeditText.Substring(0, cp),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    _textView.FontSize,
                    brush);
                return x + prefix.Width;
            }

            // 默认放在组合文本末尾.
            return x + fullText.Width;
        }
    }
}
