// 显示 IME 组合(拼音)文本的图层.
// 在富文本编辑器里, 普通的 Avalonia TextBox 通过 TextPresenter 的 PreeditText
// 在输入框内原地上显示带下划线的拼音, 且光标跟随到拼音末尾. 但 AvaloniaEdit 的
// TextArea 之前 SupportsPreedit == false, 且 SetPreeditText 是空实现, 导致 Windows
// 的 IMM32 组合字符串 (例如微信输入法输入的 "ni'hao") 根本没有被绘制出来.
// 本图层复现 TextBox 的行为: 在光标处原地上绘制带下划线的组合文本, 并把组合光标
// 画在组合文本末尾 (或 IME 提供的光标位置), 让光标跟随到拼音末尾.
//
// 垂直对齐要点 (修复"拼音抬高半截 / 组合光标变矮"):
//   正文由 TextLine 绘制, 其字形基线位于 VisualYPosition.Baseline (= pos + tl.Baseline),
//   光标盒为 TextTop..TextTop+DefaultLineHeight. 而组合 (拼音) 文本是纯拉丁字符, 若直接
//   用拼音自身字体 (如 Inconsolata) 的 FormattedText 在 TextTop 处绘制, 其 ascender 比
//   "本行主导字体 (常为雅黑等中文字体) 的行高/基线"更小, 就会整体抬高、且光标更矮.
//   因此本图层改为: (1) 把拼音基线对齐到整行的 VisualYPosition.Baseline; (2) 组合光标
//   高度直接用整行的 TextTop..TextBottom (与正文光标完全一致). 两者都不依赖拼音自身 metrics.

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
        /// 组合文本起点所在的光标矩形 (文档坐标): X = 起点, Y = TextTop, Height = TextBottom-TextTop (= 正文光标高度).
        /// </summary>
        private Rect _caretRect;

        /// <summary>
        /// 整行基线的文档 Y (VisualYPosition.Baseline, 即正文字形基线所在), 用于把拼音基线对齐到正文基线.
        /// </summary>
        private double _baselineDocY;

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
        /// <paramref name="caretRect"/> 为起点光标矩形 (文档坐标), <paramref name="baselineDocY"/> 为整行基线的文档 Y.
        /// </summary>
        public void SetPreedit(string preeditText, Rect caretRect, double baselineDocY, int? cursorPos)
        {
            if (string.IsNullOrEmpty(preeditText))
            {
                Clear();
                return;
            }

            _preeditText = preeditText;
            _caretRect = caretRect;
            _baselineDocY = baselineDocY;
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
            var x = _caretRect.X - _textView.HorizontalOffset;
            var textTop = _caretRect.Y - _textView.VerticalOffset;                 // 整行 TextTop
            var textBottom = textTop + _caretRect.Height;                          // 整行 TextBottom (= TextTop + DefaultLineHeight)
            var baselineY = _baselineDocY - _textView.VerticalOffset;              // 整行基线 (正文字形基线所在)

            // 起点在可见区域之外就不绘制 (例如光标所在行已经滚出视口).
            var viewport = _textView.Bounds.Size;
            if (x > viewport.Width || textTop > viewport.Height || textBottom < 0)
                return;

            // 关键修复: 把拼音基线对齐到"整行基线" (而不是用拼音自身 Inconsolata 的 ascender 从 TextTop 往下算),
            // 这样即使本行同时含中文 (雅黑行高更大), 拼音也与左右正文落在同一基线上, 不再"抬高半截".
            var yPinyin = baselineY - formattedText.Baseline;
            drawingContext.DrawText(formattedText, new Point(x, yPinyin));

            // 下划线, 模拟“正在输入拼音”的组合态, 紧贴拼音基线下方.
            var underlinePen = new Pen(brush, 1);
            var underlineY = baselineY + 2;
            drawingContext.DrawLine(underlinePen, new Point(x, underlineY), new Point(x + formattedText.Width, underlineY));

            // 组合光标: 以整行 TextTop..TextBottom 为基准, 上下各内缩 CaretInset, 使其比整行文本框稍矮、
            // 更贴合较矮的拼音, 同时保持与正文光标同一基线居中. 水平位置默认在组合文本末尾 (拼音输入法);
            // 若 IME 提供了光标位置则用之. (嫌长/嫌短改 CaretInset 即可.)
            const double caretInset = 2;
            var caretX = GetCaretX(x, typeface, brush, formattedText);

            if (_blink)
            {
                var caretPen = new Pen(brush, 1);
                drawingContext.DrawLine(caretPen, new Point(caretX, textTop + caretInset), new Point(caretX, textBottom - caretInset));
            }
        }

        private double GetCaretX(double x, Typeface typeface, IBrush brush, FormattedText fullText)
        {
            // 与左侧拼音之间留一小段空隙, 避免光标紧贴最后一个字符. (嫌多/嫌少改 caretGap 即可.)
            const double caretGap = 1;

            if (_cursorPos is int cp && cp >= 0 && cp < _preeditText.Length)
            {
                var prefix = new FormattedText(
                    _preeditText.Substring(0, cp),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    _textView.FontSize,
                    brush);
                return x + prefix.Width + caretGap;
            }

            // 默认放在组合文本末尾.
            return x + fullText.Width + caretGap;
        }
    }
}
