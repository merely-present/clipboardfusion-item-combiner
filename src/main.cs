using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

/*
Behavior notes

- This macro combines selected text items from ClipboardFusion History, Pinned,
	Online Recent, or Online Pinned.
- When run directly by HotKey or from the Macros tab, the text parameter is just
	the current clipboard text, so there is no real Clipboard Manager source context.
- When run from a real Clipboard Manager item list, the text parameter comes from
	the selected item and the launch list is used as the startup source.
- The Macros tab does not count as a startup source.

Startup source precedence

1. Explicit inputText override using StartupSourceArgumentPrefix.
	 Example: cf-combine-source:pinned
2. Clipboard Manager launch context for real item lists.
3. Last remembered source from ScriptSettings.

Wrapper macro example

Use a tiny child macro if you want a dedicated HotKey or entry point for a
specific startup source without copying this whole macro:

string textOut;
return BFS.ClipboardFusion.RunMacro(
	"Your Main Macro Name Here",
	"cf-combine-source:pinned",
	out textOut)
	? textOut
	: null;

Other behavior

- Selection text is whitespace-tolerant, but duplicate or overlapping entries are invalid.
	Example: 1-5,3 fails because 3 is already covered.
- History tooltips are vertically centered to the hovered row and sit flush to the
	left of the row when they fit, otherwise they flip to the right.
- Returning null means ClipboardFusion should leave the clipboard unchanged.
*/
public static class ClipboardFusionHelper
{
	private const string LastSourceSettingName = "CombineHistory.LastSource";
	private const string StartupSourceArgumentPrefix = "cf-combine-source:";

	private const int HistoryPanelMinimumWidth = 180;
	private const int ControlsPanelMinimumWidth = 300;
	private const int MinimumFormWidth = 580;
	private const int MinimumFormHeight = 360;
	private const int InitialFormSizeRatioNumerator = 2;
	private const int InitialFormSizeRatioDenominator = 5;
	private const int SelectionTextBoxMinimumLineCount = 2;
	private const int SelectionTextBoxVerticalPadding = 8;
	private const int SourceSelectorWidthPadding = 24;
	private const int ControlsPanelWidthPadding = 8;

	private const int SelectionUpdateDelayMilliseconds = 10;
	private const int FilterUpdateDelayMilliseconds = 250;
	private const int FilterCancellationDelayMilliseconds = 3000;
	private const int FilterRegexTimeoutSeconds = 1;
	private const int HistoryToolTipAutoPopDelayMilliseconds = 8000;
	private const int HistoryToolTipHoverDelayMilliseconds = 150;
	private const int HelpToolTipAutoPopDelayMilliseconds = 12000;
	private const int HelpToolTipDelayMilliseconds = 200;

	private const int MaximumToolTipTextLength = 1500;
	private const int ToolTipMaximumWidth = 720;
	private const int ToolTipPadding = 8;
	private const int ToolTipTextInset = 4;
	private const string ToolTipNewLinePrefix = "+|";
	private const string ToolTipContinuationPrefix = " |";

	private const RegexOptions DefaultFilterRegexOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
	private const string FilterPlaceholderText = "Default: IgnoreCase + Singleline";
	private const string RegexReferenceUrl = "https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-language-quick-reference#regular-expression-options";
	private const string RegexOptionsToolTipText =
		"Inline options\r\n" +
		"  Apply from the point of use to the end of the pattern.\r\n" +
		"  Syntax: `(?imnsx-imnsx)`\r\n" +
		"\r\n" +
		"  `i`  Case-insensitive matching\r\n" +
		"  `m`  Multiline: `^` and `$` match line boundaries\r\n" +
		"  `n`  Do not capture unnamed groups\r\n" +
		"  `s`  Single-line: `.` matches `\\n`  (on by default here)\r\n" +
		"  `x`  Ignore unescaped whitespace in the pattern\r\n" +
		"\r\n" +
		"  Disable with `-` inside the option list: `(?-i)` turns `i` off\r\n" +
		"\r\n" +
		"Group-scoped form\r\n" +
		"  Syntax: `(?imnsx-imnsx:subexpression)`\r\n" +
		"  The `:` form changes how that group is read only.\r\n" +
		"  Applies options only inside that group.\r\n" +
		"  Example: `(?i:\\w+)` matches word chars case-insensitively";
	private const string PrefixModeButtonText = "Prefix";
	private const string NumberedModeButtonText = "Numbered";
	private const string OutputModeToolTipText =
		"Click to switch modes\r\n" +
		"\r\n" +
		"Prefix\r\n" +
		"  Adds the text box value before each selected item\r\n" +
		"\r\n" +
		"Numbered\r\n" +
		"  Adds 1, 2, 3... followed by the text box value";

	private static readonly string[] SourceSelectorOptions = new string[] { "History", "Pinned", "Online Recent", "Online Pinned" };
	private static readonly Dictionary<string, ClipboardManagerSource> ClipboardManagerSourceAliases = new Dictionary<string, ClipboardManagerSource> {
		{ "history", ClipboardManagerSource.History },
		{ "pinned", ClipboardManagerSource.Pinned },
		{ "localpinned", ClipboardManagerSource.Pinned },
		{ "recent", ClipboardManagerSource.OnlineRecent },
		{ "saved", ClipboardManagerSource.OnlineRecent },
		{ "onlinerecent", ClipboardManagerSource.OnlineRecent },
		{ "onlinepinned", ClipboardManagerSource.OnlinePinned }
	};
	private static readonly TextFormatFlags ToolTipTextFormat = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
	private static readonly TextFormatFlags ToolTipSingleLineTextFormat = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
	private static readonly Color InlineOptionsHintBackColor = Color.FromArgb(255, 248, 214);
	private static readonly Color InlineOptionsHintForeColor = Color.FromArgb(118, 101, 43);
	private static readonly Color SelectionValidColor = Color.FromArgb(230, 255, 230);
	private static readonly Color SelectionInvalidColor = Color.FromArgb(255, 232, 232);
	private static readonly Color FilterValidColor = Color.FromArgb(230, 255, 230);
	private static readonly Color FilterInvalidColor = Color.FromArgb(255, 232, 232);
	private static readonly Color FilterBusyColor = Color.FromArgb(255, 251, 220);
	private static readonly Color FilterWarningColor = Color.FromArgb(255, 239, 213);

	private sealed class CoalescingListBox : ListBox
	{
		private const int WheelMessage = 0x020A;
		private const int WheelDeltaUnit = 120;

		private int pendingWheelDelta;
		private bool isWheelUpdateQueued;

		protected override void WndProc(ref Message message)
		{
			if (message.Msg == WheelMessage)
			{
				int wheelDelta = (short)((message.WParam.ToInt64() >> 16) & 0xFFFF);
				pendingWheelDelta += wheelDelta;
				Point mouseLocation = PointToClient(new Point((short)(message.LParam.ToInt64() & 0xFFFF), (short)((message.LParam.ToInt64() >> 16) & 0xFFFF)));
				OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, mouseLocation.X, mouseLocation.Y, wheelDelta));
				QueueWheelUpdate();
				return;
			}

			base.WndProc(ref message);
		}

		private void QueueWheelUpdate()
		{
			if (isWheelUpdateQueued)
			{
				return;
			}

			isWheelUpdateQueued = true;
			BeginInvoke(new MethodInvoker(ApplyPendingWheelDelta));
		}

		private void ApplyPendingWheelDelta()
		{
			isWheelUpdateQueued = false;
			if (!IsHandleCreated || IsDisposed)
			{
				pendingWheelDelta = 0;
				return;
			}

			int wheelNotches = pendingWheelDelta / WheelDeltaUnit;
			pendingWheelDelta %= WheelDeltaUnit;
			if (wheelNotches == 0 || Items.Count == 0)
			{
				return;
			}

			int linesPerWheelNotch = SystemInformation.MouseWheelScrollLines;
			if (linesPerWheelNotch == -1)
			{
				linesPerWheelNotch = GetVisibleItemCount();
			}
			if (linesPerWheelNotch == 0)
			{
				pendingWheelDelta = 0;
				return;
			}
			if (linesPerWheelNotch < 0)
			{
				linesPerWheelNotch = 1;
			}

			int requestedLineDelta = wheelNotches * linesPerWheelNotch;
			int visibleItemCount = GetVisibleItemCount();
			int maximumTopIndex = Math.Max(Items.Count - visibleItemCount, 0);
			int targetTopIndex = Math.Max(0, Math.Min(maximumTopIndex, TopIndex - requestedLineDelta));
			if (targetTopIndex != TopIndex)
			{
				TopIndex = targetTopIndex;
			}

			if (pendingWheelDelta / WheelDeltaUnit != 0)
			{
				QueueWheelUpdate();
			}
		}

		private int GetVisibleItemCount()
		{
			int itemHeight = Math.Max(ItemHeight, 1);
			return Math.Max(ClientSize.Height / itemHeight, 1);
		}
	}

	private enum FilterPreparationStatus
	{
		Empty,
		Ready,
		Invalid
	}

	private enum ClipboardManagerSource
	{
		History,
		Pinned,
		OnlineRecent,
		OnlinePinned
	}

	private sealed class FilterExecutionResult
	{
		public List<int> VisibleHistoryIndices;
		public bool TimedOut;
	}

	private static bool TryGetClipboardManagerSourceByIndex(int sourceIndex, out ClipboardManagerSource source)
	{
		if (sourceIndex >= 0 && sourceIndex < SourceSelectorOptions.Length)
		{
			source = (ClipboardManagerSource)sourceIndex;
			return true;
		}

		source = ClipboardManagerSource.History;
		return false;
	}

	private static string NormalizeClipboardManagerSourceText(string sourceText)
	{
		return string.IsNullOrWhiteSpace(sourceText) ? string.Empty : sourceText.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
	}

	private static bool TryParseClipboardManagerSource(string sourceText, out ClipboardManagerSource source)
	{
		source = ClipboardManagerSource.History;
		return ClipboardManagerSourceAliases.TryGetValue(NormalizeClipboardManagerSourceText(sourceText), out source);
	}

	private static bool TryGetRequestedClipboardManagerSource(string inputText, out ClipboardManagerSource source)
	{
		source = ClipboardManagerSource.History;
		if (string.IsNullOrWhiteSpace(inputText))
		{
			return false;
		}

		string trimmedInputText = inputText.Trim();
		if (!trimmedInputText.StartsWith(StartupSourceArgumentPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return TryParseClipboardManagerSource(trimmedInputText.Substring(StartupSourceArgumentPrefix.Length), out source);
	}

	private static bool TryGetLaunchClipboardManagerSource(out ClipboardManagerSource source)
	{
		source = ClipboardManagerSource.History;
		try
		{
			if (BFS.ClipboardFusion.GetClipboardManagerSelectedIndex() < 0)
			{
				return false;
			}

			return TryParseClipboardManagerSource(BFS.ClipboardFusion.GetSelectedClipboardManagerList().ToString(), out source);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static ClipboardManagerSource GetRememberedClipboardManagerSource()
	{
		try
		{
			ClipboardManagerSource rememberedSource;
			return TryGetClipboardManagerSourceByIndex(BFS.ScriptSettings.ReadValueInt(LastSourceSettingName), out rememberedSource) ? rememberedSource : ClipboardManagerSource.History;
		}
		catch (Exception)
		{
			return ClipboardManagerSource.History;
		}
	}

	private static void SaveRememberedClipboardManagerSource(ClipboardManagerSource source)
	{
		try
		{
			BFS.ScriptSettings.WriteValueInt(LastSourceSettingName, (int)source);
		}
		catch (Exception)
		{
		}
	}

	private static ClipboardManagerSource GetInitialClipboardManagerSource(string inputText)
	{
		ClipboardManagerSource requestedSource;
		if (TryGetRequestedClipboardManagerSource(inputText, out requestedSource))
		{
			return requestedSource;
		}

		ClipboardManagerSource launchSource;
		if (TryGetLaunchClipboardManagerSource(out launchSource))
		{
			return launchSource;
		}

		return GetRememberedClipboardManagerSource();
	}

	private static List<string> GetClipboardManagerItems(ClipboardManagerSource source)
	{
		IEnumerable<string> itemTexts;
		switch (source)
		{
			case ClipboardManagerSource.History:
				itemTexts = BFS.ClipboardFusion.GetAllHistoryText();
				break;
			case ClipboardManagerSource.Pinned:
				itemTexts = BFS.ClipboardFusion.GetAllLocalPinnedText();
				break;
			case ClipboardManagerSource.OnlineRecent:
				itemTexts = BFS.ClipboardFusion.CFOGetAllSavedText();
				break;
			case ClipboardManagerSource.OnlinePinned:
				itemTexts = BFS.ClipboardFusion.CFOGetAllPinnedText();
				break;
			default:
				itemTexts = Array.Empty<string>();
				break;
		}

		return (itemTexts ?? Array.Empty<string>()).Where(itemText => !string.IsNullOrEmpty(itemText)).ToList();
	}

	private static List<int> GetAllHistoryIndices(int itemCount)
	{
		return Enumerable.Range(0, itemCount).ToList();
	}

	private static string FlattenHistoryText(string historyText)
	{
		return historyText.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
	}

	private static string FormatHistoryListEntryText(int historyIndex, string historyText, int historyNumberWidth)
	{
		return (historyIndex + 1).ToString().PadLeft(historyNumberWidth) + " | " + FlattenHistoryText(historyText);
	}

	private static void PopulateHistoryListBox(ListBox historyListBox, IList<string> historyItems, IList<int> visibleHistoryIndices)
	{
		int historyNumberWidth = Math.Max(historyItems.Count, 1).ToString().Length;
		historyListBox.BeginUpdate();
		historyListBox.Items.Clear();
		foreach (int historyIndex in visibleHistoryIndices)
		{
			historyListBox.Items.Add(FormatHistoryListEntryText(historyIndex, historyItems[historyIndex], historyNumberWidth));
		}
		historyListBox.EndUpdate();
	}

	private static FilterPreparationStatus TryPrepareFilterMatcher(string filterText, out Func<string, bool> matcher)
	{
		matcher = null;
		if (string.IsNullOrWhiteSpace(filterText))
		{
			return FilterPreparationStatus.Empty;
		}

		try
		{
			var compiledRegex = new Regex(filterText, DefaultFilterRegexOptions, TimeSpan.FromSeconds(FilterRegexTimeoutSeconds));
			matcher = text => compiledRegex.IsMatch(text);
			return FilterPreparationStatus.Ready;
		}
		catch (ArgumentException)
		{
			return FilterPreparationStatus.Invalid;
		}
	}

	private static bool TryUnescapeSeparatorText(string separatorText, out string unescapedSeparatorText)
	{
		try
		{
			unescapedSeparatorText = Regex.Unescape(separatorText ?? string.Empty);
			return true;
		}
		catch (ArgumentException)
		{
			unescapedSeparatorText = string.Empty;
			return false;
		}
	}

	private static FilterExecutionResult ExecuteFilter(IList<string> historyItems, Func<string, bool> matcher, CancellationToken cancellationToken)
	{
		var visibleHistoryIndices = new List<int>();
		for (int historyIndex = 0; historyIndex < historyItems.Count; historyIndex++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (matcher(historyItems[historyIndex]))
			{
				visibleHistoryIndices.Add(historyIndex);
			}
		}

		return new FilterExecutionResult { VisibleHistoryIndices = visibleHistoryIndices, TimedOut = false };
	}

	private static FilterExecutionResult CreateTimedOutFilterResult()
	{
		return new FilterExecutionResult { VisibleHistoryIndices = null, TimedOut = true };
	}

	private static FilterExecutionResult ExecuteFilterSafely(IList<string> historyItems, Func<string, bool> matcher, CancellationToken cancellationToken)
	{
		try
		{
			return ExecuteFilter(historyItems, matcher, cancellationToken);
		}
		catch (Exception)
		{
			return CreateTimedOutFilterResult();
		}
	}

	private static Size MeasureToolTipSize(string toolTipText, Font toolTipFont)
	{
		Size textSize = TextRenderer.MeasureText(toolTipText ?? string.Empty, toolTipFont, new Size(ToolTipMaximumWidth, 0), ToolTipTextFormat);
		return new Size(textSize.Width + ToolTipPadding, textSize.Height + ToolTipPadding);
	}

	private static void DrawToolTipText(DrawToolTipEventArgs eventArgs, string toolTipText, Font toolTipFont)
	{
		eventArgs.DrawBackground();
		eventArgs.DrawBorder();
		TextRenderer.DrawText(eventArgs.Graphics, toolTipText ?? string.Empty, toolTipFont, Rectangle.Inflate(eventArgs.Bounds, -ToolTipTextInset, -ToolTipTextInset), SystemColors.InfoText, ToolTipTextFormat);
	}

	// Match Clipboard Manager preview placement: sit flush to the row on the left when possible,
	// otherwise flip to the right, and fall back to centered when neither side has room.
	private static Point GetHistoryToolTipLocation(ListBox historyListBox, int hoverIndex, Size toolTipSize)
	{
		Rectangle itemBounds = historyListBox.GetItemRectangle(hoverIndex);
		Rectangle itemScreenBounds = historyListBox.RectangleToScreen(itemBounds);
		Rectangle screenBounds = Screen.FromPoint(new Point(itemScreenBounds.Left, itemScreenBounds.Top)).WorkingArea;
		int maximumScreenX = Math.Max(screenBounds.Left, screenBounds.Right - toolTipSize.Width);
		int maximumScreenY = Math.Max(screenBounds.Top, screenBounds.Bottom - toolTipSize.Height);

		int leftScreenX = itemScreenBounds.Left - toolTipSize.Width;
		int rightScreenX = itemScreenBounds.Right;
		int toolTipScreenX;
		if (leftScreenX >= screenBounds.Left)
		{
			toolTipScreenX = leftScreenX;
		}
		else if (rightScreenX <= maximumScreenX)
		{
			toolTipScreenX = rightScreenX;
		}
		else
		{
			toolTipScreenX = screenBounds.Left + (maximumScreenX - screenBounds.Left) / 2;
		}

		Point rowCenterScreenLocation = historyListBox.PointToScreen(new Point(0, itemBounds.Top + (itemBounds.Height - toolTipSize.Height) / 2));
		int toolTipScreenY = Math.Max(screenBounds.Top, Math.Min(maximumScreenY, rowCenterScreenLocation.Y));
		return historyListBox.PointToClient(new Point(toolTipScreenX, toolTipScreenY));
	}

	private static int MeasureToolTipLineWidth(string toolTipLineText, Font toolTipFont)
	{
		return TextRenderer.MeasureText(toolTipLineText ?? string.Empty, toolTipFont, Size.Empty, ToolTipSingleLineTextFormat).Width;
	}

	private static bool DoesToolTipLineFit(string linePrefix, string lineContent, Font toolTipFont, int maximumLineWidth)
	{
		return MeasureToolTipLineWidth(linePrefix + lineContent, toolTipFont) <= maximumLineWidth;
	}

	private static int FindLongestToolTipSegmentLength(string lineWord, int startIndex, string linePrefix, Font toolTipFont, int maximumLineWidth)
	{
		int longestSegmentLength = 1;
		int lowerBound = 1;
		int upperBound = lineWord.Length - startIndex;
		while (lowerBound <= upperBound)
		{
			int candidateSegmentLength = lowerBound + (upperBound - lowerBound) / 2;
			if (DoesToolTipLineFit(linePrefix, lineWord.Substring(startIndex, candidateSegmentLength), toolTipFont, maximumLineWidth))
			{
				longestSegmentLength = candidateSegmentLength;
				lowerBound = candidateSegmentLength + 1;
			}
			else
			{
				upperBound = candidateSegmentLength - 1;
			}
		}

		return longestSegmentLength;
	}

	private static void AttachHistoryToolTips(ListBox historyListBox, Func<int, string> getHistoryItemText, Font toolTipFont)
	{
		string activeToolTipText = string.Empty;
		string pendingToolTipText = string.Empty;
		var historyToolTip = new ToolTip { AutoPopDelay = HistoryToolTipAutoPopDelayMilliseconds, UseAnimation = false, UseFading = false, OwnerDraw = true };
		var historyToolTipTimer = new Timer { Interval = HistoryToolTipHoverDelayMilliseconds };
		int hoveredToolTipIndex = -1;
		int pendingToolTipIndex = -1;

		historyToolTip.Popup += (sender, eventArgs) => {
			eventArgs.ToolTipSize = MeasureToolTipSize(activeToolTipText, toolTipFont);
		};
		historyToolTip.Draw += (sender, eventArgs) => {
			DrawToolTipText(eventArgs, activeToolTipText, toolTipFont);
		};

		Action resetHistoryToolTip = () => {
			historyToolTipTimer.Stop();
			historyToolTip.Hide(historyListBox);
			activeToolTipText = string.Empty;
			pendingToolTipText = string.Empty;
			pendingToolTipIndex = -1;
		};
		Func<int, string> getWrappedHistoryToolTipText = hoverIndex => {
			if (hoverIndex < 0 || hoverIndex >= historyListBox.Items.Count)
			{
				return null;
			}

			string fullTooltipText = getHistoryItemText(hoverIndex);
			if (string.IsNullOrEmpty(fullTooltipText))
			{
				return null;
			}
			if (fullTooltipText.Length > MaximumToolTipTextLength)
			{
				fullTooltipText = fullTooltipText.Substring(0, MaximumToolTipTextLength);
			}

			return WrapTooltipText(fullTooltipText, toolTipFont, ToolTipMaximumWidth);
		};
		Action<Point> scheduleHistoryToolTip = cursorLocation => {
			int hoverIndex = historyListBox.IndexFromPoint(cursorLocation);
			if (hoverIndex < 0 || hoverIndex >= historyListBox.Items.Count)
			{
				hoveredToolTipIndex = -1;
				resetHistoryToolTip();
				return;
			}

			if (hoverIndex == hoveredToolTipIndex)
			{
				return;
			}

			string wrappedToolTipText = getWrappedHistoryToolTipText(hoverIndex);
			if (string.IsNullOrEmpty(wrappedToolTipText))
			{
				hoveredToolTipIndex = -1;
				resetHistoryToolTip();
				return;
			}

			hoveredToolTipIndex = hoverIndex;
			resetHistoryToolTip();
			pendingToolTipIndex = hoverIndex;
			pendingToolTipText = wrappedToolTipText;
			historyToolTipTimer.Start();
		};
		historyToolTipTimer.Tick += (sender, eventArgs) => {
			historyToolTipTimer.Stop();
			Point cursorLocation = historyListBox.PointToClient(Cursor.Position);
			int hoverIndex = historyListBox.IndexFromPoint(cursorLocation);
			if (hoverIndex != hoveredToolTipIndex || hoverIndex != pendingToolTipIndex || string.IsNullOrEmpty(pendingToolTipText))
			{
				return;
			}

			activeToolTipText = pendingToolTipText;
			Size toolTipSize = MeasureToolTipSize(activeToolTipText, toolTipFont);
			Point toolTipLocation = GetHistoryToolTipLocation(historyListBox, hoverIndex, toolTipSize);
			historyToolTip.Show(activeToolTipText, historyListBox, toolTipLocation.X, toolTipLocation.Y, historyToolTip.AutoPopDelay);
		};
		historyListBox.MouseMove += (sender, eventArgs) => {
			scheduleHistoryToolTip(eventArgs.Location);
		};
		historyListBox.MouseLeave += (sender, eventArgs) => {
			resetHistoryToolTip();
			hoveredToolTipIndex = -1;
		};
		historyListBox.MouseWheel += (sender, eventArgs) => {
			scheduleHistoryToolTip(historyListBox.PointToClient(Cursor.Position));
		};
		historyListBox.Disposed += (sender, eventArgs) => {
			historyToolTipTimer.Dispose();
			historyToolTip.Dispose();
		};
	}

	private static ToolTip CreateMonospaceToolTip(Font toolTipFont, int autoPopDelay, int initialDelay, int reshowDelay)
	{
		var monospaceToolTip = new ToolTip {
			AutoPopDelay = autoPopDelay,
			InitialDelay = initialDelay,
			ReshowDelay = reshowDelay,
			UseAnimation = false,
			UseFading = false,
			OwnerDraw = true
		};
		monospaceToolTip.Popup += (sender, eventArgs) => {
			ToolTip sourceToolTip = sender as ToolTip;
			string toolTipText = sourceToolTip == null ? string.Empty : sourceToolTip.GetToolTip(eventArgs.AssociatedControl) ?? string.Empty;
			eventArgs.ToolTipSize = MeasureToolTipSize(toolTipText, toolTipFont);
		};
		monospaceToolTip.Draw += (sender, eventArgs) => {
			ToolTip sourceToolTip = sender as ToolTip;
			string toolTipText = sourceToolTip == null ? string.Empty : sourceToolTip.GetToolTip(eventArgs.AssociatedControl) ?? string.Empty;
			DrawToolTipText(eventArgs, toolTipText, toolTipFont);
		};
		return monospaceToolTip;
	}

	private static void SetTextBoxPlaceholder(TextBox textBox, string cueText)
	{
		if (textBox == null)
		{
			return;
		}

		textBox.PlaceholderText = cueText ?? string.Empty;
	}

	private static Control GetFocusedControl(Control rootControl)
	{
		ContainerControl containerControl = rootControl as ContainerControl;
		while (containerControl != null && containerControl.ActiveControl != null)
		{
			rootControl = containerControl.ActiveControl;
			containerControl = rootControl as ContainerControl;
		}
		return rootControl;
	}

	private static string FormatSelectionText(IList<int> selectedIndices)
	{
		if (selectedIndices == null || selectedIndices.Count == 0)
		{
			return string.Empty;
		}

		var orderedSelectionIndices = selectedIndices.Distinct().OrderBy(selectionIndex => selectionIndex).ToList();
		var selectionParts = new List<string>();
		int rangeStartIndex = orderedSelectionIndices[0];
		int rangeEndIndex = orderedSelectionIndices[0];
		foreach (int selectedIndex in orderedSelectionIndices.Skip(1))
		{
			if (selectedIndex == rangeEndIndex + 1)
			{
				rangeEndIndex = selectedIndex;
				continue;
			}

			selectionParts.Add(rangeStartIndex == rangeEndIndex ? (rangeStartIndex + 1).ToString() : $"{rangeStartIndex + 1}-{rangeEndIndex + 1}");
			rangeStartIndex = rangeEndIndex = selectedIndex;
		}

		selectionParts.Add(rangeStartIndex == rangeEndIndex ? (rangeStartIndex + 1).ToString() : $"{rangeStartIndex + 1}-{rangeEndIndex + 1}");
		return string.Join(", ", selectionParts);
	}

	private static bool TryParseSelectionText(string selectionText, int itemCount, out List<int> parsedSelectionIndices, out string normalizedSelectionText)
	{
		parsedSelectionIndices = new List<int>();
		normalizedSelectionText = string.Empty;
		if (string.IsNullOrWhiteSpace(selectionText))
		{
			return false;
		}

		var selectedIndexSet = new SortedSet<int>();
		foreach (string rawSelectionPart in selectionText.Split(','))
		{
			string selectionPart = rawSelectionPart.Trim();
			if (selectionPart.Length == 0)
			{
				return false;
			}

			int dashIndex = selectionPart.IndexOf('-');
			if (dashIndex >= 0)
			{
				if (selectionPart.IndexOf('-', dashIndex + 1) >= 0)
				{
					return false;
				}

				string rangeStartText = selectionPart.Substring(0, dashIndex).Trim();
				string rangeEndText = selectionPart.Substring(dashIndex + 1).Trim();
				int rangeStartNumber;
				int rangeEndNumber;
				if (!int.TryParse(rangeStartText, out rangeStartNumber) || !int.TryParse(rangeEndText, out rangeEndNumber))
				{
					return false;
				}
				if (rangeStartNumber < 1 || rangeEndNumber < rangeStartNumber || rangeEndNumber > itemCount)
				{
					return false;
				}

				for (int listIndex = rangeStartNumber - 1; listIndex < rangeEndNumber; listIndex++)
				{
					if (!selectedIndexSet.Add(listIndex))
					{
						return false;
					}
				}
			}
			else
			{
				int selectionNumber;
				if (!int.TryParse(selectionPart, out selectionNumber) || selectionNumber < 1 || selectionNumber > itemCount)
				{
					return false;
				}
				if (!selectedIndexSet.Add(selectionNumber - 1))
				{
					return false;
				}
			}
		}

		if (selectedIndexSet.Count == 0)
		{
			return false;
		}

		parsedSelectionIndices = selectedIndexSet.ToList();
		normalizedSelectionText = FormatSelectionText(parsedSelectionIndices);
		return true;
	}

	private static string WrapTooltipText(string tooltipText, Font toolTipFont, int maximumLineWidth)
	{
		if (string.IsNullOrEmpty(tooltipText) || toolTipFont == null || maximumLineWidth < 1)
		{
			return tooltipText;
		}

		string[] sourceLines = tooltipText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		var wrappedLines = new List<string>();
		for (int sourceLineIndex = 0; sourceLineIndex < sourceLines.Length; sourceLineIndex++)
		{
			string sourceLine = sourceLines[sourceLineIndex];
			if (sourceLine.Length == 0)
			{
				wrappedLines.Add(ToolTipNewLinePrefix);
				continue;
			}

			string[] lineWords = sourceLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			string currentLinePrefix = ToolTipNewLinePrefix;
			var currentLineContent = new StringBuilder();
			for (int wordIndex = 0; wordIndex < lineWords.Length; wordIndex++)
			{
				string lineWord = lineWords[wordIndex];
				if (currentLineContent.Length > 0)
				{
					string appendedLineContent = currentLineContent + " " + lineWord;
					if (DoesToolTipLineFit(currentLinePrefix, appendedLineContent, toolTipFont, maximumLineWidth))
					{
						currentLineContent.Append(' ');
						currentLineContent.Append(lineWord);
						continue;
					}

					wrappedLines.Add(currentLinePrefix + currentLineContent);
					currentLinePrefix = ToolTipContinuationPrefix;
					currentLineContent.Clear();
				}

				if (DoesToolTipLineFit(currentLinePrefix, lineWord, toolTipFont, maximumLineWidth))
				{
					currentLineContent.Append(lineWord);
					continue;
				}

				int wordOffset = 0;
				while (wordOffset < lineWord.Length)
				{
					int segmentLength = FindLongestToolTipSegmentLength(lineWord, wordOffset, currentLinePrefix, toolTipFont, maximumLineWidth);
					currentLineContent.Append(lineWord, wordOffset, segmentLength);
					wordOffset += segmentLength;
					if (wordOffset < lineWord.Length)
					{
						wrappedLines.Add(currentLinePrefix + currentLineContent);
						currentLinePrefix = ToolTipContinuationPrefix;
						currentLineContent.Clear();
					}
				}
			}

			wrappedLines.Add(currentLinePrefix + currentLineContent);
		}

		return string.Join("\n", wrappedLines);
	}

	public static string ProcessText(string inputText)
	{
		ClipboardManagerSource currentSource = GetInitialClipboardManagerSource(inputText);
		List<string> historyItems = GetClipboardManagerItems(currentSource);
		List<int> allHistoryIndices = GetAllHistoryIndices(historyItems.Count);

		Rectangle screenWorkingArea = Screen.PrimaryScreen.WorkingArea;
		int formWidth = Math.Max(MinimumFormWidth, screenWorkingArea.Width * InitialFormSizeRatioNumerator / InitialFormSizeRatioDenominator);
		int formHeight = Math.Max(MinimumFormHeight, screenWorkingArea.Height * InitialFormSizeRatioNumerator / InitialFormSizeRatioDenominator);
		var monospaceFont = new Font("Consolas", 9f);

		var combineHistoryForm = new Form {
			Text = "Combine History", Font = monospaceFont,
			Width = formWidth, Height = formHeight, MinimumSize = new Size(MinimumFormWidth, MinimumFormHeight),
			StartPosition = FormStartPosition.CenterScreen
		};
		combineHistoryForm.Disposed += (sender, eventArgs) => {
			monospaceFont.Dispose();
		};

		var historyListBox = new CoalescingListBox {
			SelectionMode = SelectionMode.MultiExtended,
			Dock = DockStyle.Fill, IntegralHeight = false,
			TabStop = false
		};
		var visibleHistoryIndices = new List<int>(allHistoryIndices);
		PopulateHistoryListBox(historyListBox, historyItems, visibleHistoryIndices);
		AttachHistoryToolTips(historyListBox, visibleListIndex => visibleListIndex >= 0 && visibleListIndex < visibleHistoryIndices.Count ? historyItems[visibleHistoryIndices[visibleListIndex]] : null, monospaceFont);

		var historyPanel = new Panel {
			Dock = DockStyle.Fill,
			Padding = new Padding(8, 8, 8, 4),
			MinimumSize = new Size(HistoryPanelMinimumWidth, 0)
		};
		historyPanel.Controls.Add(historyListBox);

		var filterTextBox = new TextBox {
			Dock = DockStyle.Fill,
			Margin = new Padding(0)
		};
		SetTextBoxPlaceholder(filterTextBox, FilterPlaceholderText);
		var inlineOptionsHintLabel = new Label {
			Text = "(?imnsx-imnsx)",
			AutoSize = true,
			BackColor = InlineOptionsHintBackColor,
			ForeColor = InlineOptionsHintForeColor,
			Padding = new Padding(3, 2, 3, 2),
			Margin = new Padding(0, 0, 8, 0),
			Cursor = Cursors.Help
		};
		var regexReferenceLinkLabel = new LinkLabel {
			Text = "(Help)",
			AutoSize = true,
			Margin = new Padding(0),
			TabStop = false
		};
		var filterHelpRow = new FlowLayoutPanel {
			Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
			FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 2), Padding = new Padding(0)
		};
		filterHelpRow.Controls.Add(inlineOptionsHintLabel);
		filterHelpRow.Controls.Add(regexReferenceLinkLabel);
		var regexHelpToolTip = CreateMonospaceToolTip(monospaceFont, HelpToolTipAutoPopDelayMilliseconds, HelpToolTipDelayMilliseconds, HelpToolTipDelayMilliseconds);
		regexHelpToolTip.SetToolTip(inlineOptionsHintLabel, RegexOptionsToolTipText);
		regexHelpToolTip.SetToolTip(regexReferenceLinkLabel, ".NET Regex Reference");
		regexReferenceLinkLabel.LinkClicked += (sender, eventArgs) => {
			try
			{
				Process.Start(new ProcessStartInfo(RegexReferenceUrl) { UseShellExecute = true });
			}
			catch (Exception)
			{
			}
		};
		combineHistoryForm.Disposed += (sender, eventArgs) => {
			regexHelpToolTip.Dispose();
		};
		int selectionTextBoxHeight = TextRenderer.MeasureText("0", monospaceFont).Height * SelectionTextBoxMinimumLineCount + SelectionTextBoxVerticalPadding;
		var selectionTextBox = new TextBox {
			Dock = DockStyle.Fill,
			Multiline = true, ScrollBars = ScrollBars.Vertical,
			MinimumSize = new Size(0, selectionTextBoxHeight),
			Margin = new Padding(0)
		};
		var resetSelectionButton = new Button {
			Text = "Reset Selection", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
		};
		var restoreValidSelectionButton = new Button {
			Text = "Restore Last Valid", Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
		};
		var selectionButtonRow = new FlowLayoutPanel {
			Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
			FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), Padding = new Padding(0)
		};
		selectionButtonRow.Controls.Add(resetSelectionButton);
		selectionButtonRow.Controls.Add(restoreValidSelectionButton);

		string prefixText = string.Empty;
		string numberSuffixText = ". ";
		bool isNumberedMode = false;
		Action<TextBox> attachSelectAllOnKeyboardFocus = textBox => {
			textBox.Enter += (sender, eventArgs) => {
				if (Control.MouseButtons != MouseButtons.None)
				{
					return;
				}

				textBox.SelectAll();
			};
		};
		var modeAffixTextBox = new TextBox {
			Text = prefixText,
			Anchor = AnchorStyles.Left | AnchorStyles.Right
		};
		attachSelectAllOnKeyboardFocus(modeAffixTextBox);
		var modeToggleButton = new Button {
			Text = PrefixModeButtonText,
			AutoSize = false,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
		};
		var separatorTextBox = new TextBox {
			Text = @"\n",
			Anchor = AnchorStyles.Left | AnchorStyles.Right
		};
		attachSelectAllOnKeyboardFocus(separatorTextBox);
		Action<Control> focusControl = control => {
			control.Focus();
			if (control is TextBox textBox)
			{
				textBox.SelectAll();
			}
		};

		Action updateModeToggleButtonText = () => {
			modeToggleButton.Text = isNumberedMode ? NumberedModeButtonText : PrefixModeButtonText;
		};
		modeAffixTextBox.TextChanged += (sender, eventArgs) => {
			if (isNumberedMode)
			{
				numberSuffixText = modeAffixTextBox.Text;
				return;
			}

			prefixText = modeAffixTextBox.Text;
		};
		modeToggleButton.Click += (sender, eventArgs) => {
			isNumberedMode = !isNumberedMode;
			modeAffixTextBox.Text = isNumberedMode ? numberSuffixText : prefixText;
			updateModeToggleButtonText();
			focusControl(modeAffixTextBox);
		};
		updateModeToggleButtonText();
		regexHelpToolTip.SetToolTip(modeToggleButton, OutputModeToolTipText);

		var controlsLayout = new TableLayoutPanel {
			Dock = DockStyle.Fill,
			Padding = Padding.Empty,
			ColumnCount = 2, RowCount = 8
		};
		controlsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		controlsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		controlsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

		var filterLabel = new Label {
			Text = "Regex (.NET):", AutoSize = true, Anchor = AnchorStyles.Left
		};
		var selectionLabel = new Label {
			Text = "Selection:", AutoSize = true, Anchor = AnchorStyles.Left
		};
		var separatorLabel = new Label {
			Text = "Separator:", AutoSize = true, Anchor = AnchorStyles.Left
		};
		controlsLayout.Controls.Add(filterLabel, 0, 0);
		controlsLayout.SetColumnSpan(filterLabel, 2);
		controlsLayout.Controls.Add(filterTextBox, 0, 1);
		controlsLayout.SetColumnSpan(filterTextBox, 2);
		controlsLayout.Controls.Add(filterHelpRow, 0, 2);
		controlsLayout.SetColumnSpan(filterHelpRow, 2);
		controlsLayout.Controls.Add(selectionLabel, 0, 3);
		controlsLayout.SetColumnSpan(selectionLabel, 2);
		controlsLayout.Controls.Add(selectionTextBox, 0, 4);
		controlsLayout.SetColumnSpan(selectionTextBox, 2);
		controlsLayout.Controls.Add(selectionButtonRow, 0, 5);
		controlsLayout.SetColumnSpan(selectionButtonRow, 2);
		controlsLayout.Controls.Add(modeToggleButton, 0, 6);
		controlsLayout.Controls.Add(modeAffixTextBox, 1, 6);
		controlsLayout.Controls.Add(separatorLabel, 0, 7);
		controlsLayout.Controls.Add(separatorTextBox, 1, 7);
		int sourceSelectorWidth = SourceSelectorOptions.Max(optionText => TextRenderer.MeasureText(optionText, monospaceFont).Width) + SystemInformation.VerticalScrollBarWidth + SourceSelectorWidthPadding;
		var sourceSelectorComboBox = new ComboBox {
			DropDownStyle = ComboBoxStyle.DropDownList,
			Width = sourceSelectorWidth,
			Anchor = AnchorStyles.Left,
			Margin = Padding.Empty
		};
		sourceSelectorComboBox.Items.AddRange(SourceSelectorOptions);
		sourceSelectorComboBox.SelectedIndex = (int)currentSource;

		var okButton = new Button {
			Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Margin = Padding.Empty
		};
		var cancelButton = new Button {
			Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Margin = Padding.Empty
		};
		var dialogActionButtonRow = new FlowLayoutPanel {
			AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
			FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right,
			Margin = Padding.Empty,
			Padding = Padding.Empty
		};
		dialogActionButtonRow.Controls.Add(cancelButton);
		dialogActionButtonRow.Controls.Add(okButton);
		var dialogFooterLayout = new TableLayoutPanel {
			Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Padding = Padding.Empty,
			ColumnCount = 2, RowCount = 1
		};
		dialogFooterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		dialogFooterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		dialogFooterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		dialogFooterLayout.Controls.Add(sourceSelectorComboBox, 0, 0);
		dialogFooterLayout.Controls.Add(dialogActionButtonRow, 1, 0);
		int controlsPanelMinimumWidth = ControlsPanelMinimumWidth;

		var controlsPanel = new Panel { Dock = DockStyle.Right, Width = controlsPanelMinimumWidth, MinimumSize = new Size(controlsPanelMinimumWidth, 0), Padding = new Padding(SystemInformation.Border3DSize.Width) };
		controlsPanel.Controls.Add(controlsLayout);
		controlsPanel.Controls.Add(dialogFooterLayout);

		var divider = new Splitter {
			Dock = DockStyle.Right,
			Width = 6,
			MinSize = controlsPanelMinimumWidth,
			MinExtra = HistoryPanelMinimumWidth,
			TabStop = false,
			BackColor = SystemColors.ControlDark
		};
		int formNonClientWidth = combineHistoryForm.Width - combineHistoryForm.ClientSize.Width;
		combineHistoryForm.MinimumSize = new Size(HistoryPanelMinimumWidth + divider.Width + controlsPanelMinimumWidth + formNonClientWidth, combineHistoryForm.MinimumSize.Height);

		string lastValidSelectionText = null;
		var selectionUpdateTimer = new Timer { Interval = SelectionUpdateDelayMilliseconds };
		var filterUpdateTimer = new Timer { Interval = FilterUpdateDelayMilliseconds };
		bool isSelectionTextUpdateSuppressed = false;
		bool isListSelectionUpdateSuppressed = false;
		bool isFilterRunning = false;
		bool isSeparatorTextValid = true;
		var selectedHistoryIndexSet = new HashSet<int>();
		CancellationTokenSource activeFilterCancellation = null;
		int nextFilterRequestId = 0;

		Action<string, Color> setSelectionTextBoxState = (text, backColor) => {
			isSelectionTextUpdateSuppressed = true;
			selectionTextBox.Text = text;
			selectionTextBox.BackColor = backColor;
			isSelectionTextUpdateSuppressed = false;
		};
		Action clearSelectionText = () => {
			setSelectionTextBoxState(string.Empty, SystemColors.Window);
		};
		Action updateControlsPanelMinimumWidth = () => {
			controlsPanelMinimumWidth = Math.Max(ControlsPanelMinimumWidth, Math.Max(controlsLayout.GetPreferredSize(Size.Empty).Width, dialogFooterLayout.GetPreferredSize(Size.Empty).Width) + ControlsPanelWidthPadding);
			controlsPanel.Width = controlsPanelMinimumWidth;
			controlsPanel.MinimumSize = new Size(controlsPanelMinimumWidth, 0);
			divider.MinSize = controlsPanelMinimumWidth;
			combineHistoryForm.MinimumSize = new Size(HistoryPanelMinimumWidth + divider.Width + controlsPanelMinimumWidth + formNonClientWidth, combineHistoryForm.MinimumSize.Height);
		};

		Action updateControlStates = () => {
			bool hasLastValidSelection = !string.IsNullOrEmpty(lastValidSelectionText);
			filterTextBox.ReadOnly = isFilterRunning;
			sourceSelectorComboBox.Enabled = !isFilterRunning;
			historyListBox.Enabled = !isFilterRunning;
			selectionTextBox.Enabled = !isFilterRunning;
			resetSelectionButton.Enabled = !isFilterRunning;
			restoreValidSelectionButton.Enabled = !isFilterRunning && hasLastValidSelection;
			modeToggleButton.Enabled = !isFilterRunning;
			modeAffixTextBox.Enabled = !isFilterRunning;
			separatorTextBox.Enabled = !isFilterRunning;
			okButton.Enabled = !isFilterRunning && isSeparatorTextValid;
			divider.Enabled = !isFilterRunning;
		};
		Action updateSeparatorTextState = () => {
			string unescapedSeparatorText;
			isSeparatorTextValid = TryUnescapeSeparatorText(separatorTextBox.Text, out unescapedSeparatorText);
			separatorTextBox.BackColor = isSeparatorTextValid ? SystemColors.Window : FilterInvalidColor;
			updateControlStates();
		};
		Action cancelActiveFilter = () => {
			if (activeFilterCancellation == null)
			{
				return;
			}

			activeFilterCancellation.Cancel();
			activeFilterCancellation.Dispose();
			activeFilterCancellation = null;
		};
		Action syncHistoryListSelectionFromSet = () => {
			isListSelectionUpdateSuppressed = true;
			historyListBox.BeginUpdate();
			historyListBox.ClearSelected();
			for (int visibleListIndex = 0; visibleListIndex < visibleHistoryIndices.Count; visibleListIndex++)
			{
				if (selectedHistoryIndexSet.Contains(visibleHistoryIndices[visibleListIndex]))
				{
					historyListBox.SetSelected(visibleListIndex, true);
				}
			}
			historyListBox.EndUpdate();
			isListSelectionUpdateSuppressed = false;
		};
		Action updateSelectionTextFromSet = () => {
			var selectedHistoryIndices = selectedHistoryIndexSet.OrderBy(selectionIndex => selectionIndex).ToList();
			if (selectedHistoryIndices.Count == 0)
			{
				setSelectionTextBoxState(string.Empty, SystemColors.Window);
			}
			else
			{
				lastValidSelectionText = FormatSelectionText(selectedHistoryIndices);
				setSelectionTextBoxState(lastValidSelectionText, SelectionValidColor);
			}
			updateControlStates();
		};
		Action applyVisibleFilterResults = () => {
			isListSelectionUpdateSuppressed = true;
			PopulateHistoryListBox(historyListBox, historyItems, visibleHistoryIndices);
			isListSelectionUpdateSuppressed = false;
			syncHistoryListSelectionFromSet();
		};
		Action runFilter = () => {
			filterUpdateTimer.Stop();
			Func<string, bool> filterMatcher;
			FilterPreparationStatus filterStatus = TryPrepareFilterMatcher(filterTextBox.Text, out filterMatcher);
			if (filterStatus == FilterPreparationStatus.Empty)
			{
				cancelActiveFilter();
				visibleHistoryIndices = new List<int>(allHistoryIndices);
				filterTextBox.BackColor = SystemColors.Window;
				isFilterRunning = false;
				applyVisibleFilterResults();
				updateControlStates();
				return;
			}
			if (filterStatus == FilterPreparationStatus.Invalid)
			{
				filterTextBox.BackColor = FilterInvalidColor;
				isFilterRunning = false;
				updateControlStates();
				return;
			}

			cancelActiveFilter();
			isFilterRunning = true;
			filterTextBox.BackColor = FilterBusyColor;
			updateControlStates();
			int filterRequestId = ++nextFilterRequestId;
			activeFilterCancellation = new CancellationTokenSource();
			activeFilterCancellation.CancelAfter(FilterCancellationDelayMilliseconds);
			CancellationToken filterCancellationToken = activeFilterCancellation.Token;

			Task.Run(() => ExecuteFilterSafely(historyItems, filterMatcher, filterCancellationToken)).ContinueWith(filterTask => {
				if (combineHistoryForm.IsDisposed || !combineHistoryForm.IsHandleCreated)
				{
					return;
				}

				try
				{
					combineHistoryForm.BeginInvoke(new MethodInvoker(() => {
						if (combineHistoryForm.IsDisposed || filterRequestId != nextFilterRequestId)
						{
							return;
						}

						isFilterRunning = false;
						cancelActiveFilter();
						FilterExecutionResult filterResult = filterTask.Result;
						if (filterResult.TimedOut || filterResult.VisibleHistoryIndices == null)
						{
							filterTextBox.BackColor = FilterWarningColor;
							updateControlStates();
							return;
						}

						visibleHistoryIndices = filterResult.VisibleHistoryIndices;
						filterTextBox.BackColor = FilterValidColor;
						applyVisibleFilterResults();
						updateControlStates();
					}));
				}
				catch (InvalidOperationException)
				{
				}
			});
		};

		Action applyCurrentSource = () => {
			cancelActiveFilter();
			selectionUpdateTimer.Stop();
			historyItems = GetClipboardManagerItems(currentSource);
			allHistoryIndices = GetAllHistoryIndices(historyItems.Count);
			visibleHistoryIndices = new List<int>(allHistoryIndices);
			selectedHistoryIndexSet.Clear();
			lastValidSelectionText = null;
			clearSelectionText();

			Func<string, bool> filterMatcher;
			FilterPreparationStatus filterStatus = TryPrepareFilterMatcher(filterTextBox.Text, out filterMatcher);
			if (filterStatus == FilterPreparationStatus.Ready)
			{
				runFilter();
				return;
			}

			filterTextBox.BackColor = filterStatus == FilterPreparationStatus.Invalid ? FilterInvalidColor : SystemColors.Window;
			isFilterRunning = false;
			applyVisibleFilterResults();
			updateControlStates();
		};

		Action updateSelectionTextFromList = () => {
			if (isListSelectionUpdateSuppressed)
			{
				return;
			}

			foreach (int visibleHistoryIndex in visibleHistoryIndices)
			{
				selectedHistoryIndexSet.Remove(visibleHistoryIndex);
			}
			foreach (int selectedVisibleListIndex in historyListBox.SelectedIndices.Cast<int>())
			{
				selectedHistoryIndexSet.Add(visibleHistoryIndices[selectedVisibleListIndex]);
			}

			updateSelectionTextFromSet();
		};

		Action applySelectionText = () => {
			if (isSelectionTextUpdateSuppressed)
			{
				return;
			}

			string selectionText = selectionTextBox.Text.Trim();
			if (selectionText.Length == 0)
			{
				selectionTextBox.BackColor = SystemColors.Window;
				return;
			}

			List<int> parsedSelectionIndices;
			string normalizedSelectionText;
			if (!TryParseSelectionText(selectionText, historyItems.Count, out parsedSelectionIndices, out normalizedSelectionText))
			{
				selectionTextBox.BackColor = SelectionInvalidColor;
				updateControlStates();
				return;
			}

			selectionTextBox.BackColor = SelectionValidColor;
			lastValidSelectionText = normalizedSelectionText;
			selectedHistoryIndexSet = new HashSet<int>(parsedSelectionIndices);
			syncHistoryListSelectionFromSet();
			updateControlStates();
		};

		selectionUpdateTimer.Tick += (sender, eventArgs) => {
			selectionUpdateTimer.Stop();
			applySelectionText();
		};
		selectionTextBox.TextChanged += (sender, eventArgs) => {
			if (isSelectionTextUpdateSuppressed)
			{
				return;
			}

			selectionUpdateTimer.Stop();
			if (string.IsNullOrWhiteSpace(selectionTextBox.Text))
			{
				selectionTextBox.BackColor = SystemColors.Window;
				return;
			}

			selectionTextBox.BackColor = SystemColors.Window;
			selectionUpdateTimer.Start();
		};
		restoreValidSelectionButton.Click += (sender, eventArgs) => {
			if (string.IsNullOrEmpty(lastValidSelectionText))
			{
				return;
			}

			selectionUpdateTimer.Stop();
			setSelectionTextBoxState(lastValidSelectionText, SelectionValidColor);
			applySelectionText();
			focusControl(selectionTextBox);
		};
		resetSelectionButton.Click += (sender, eventArgs) => {
			selectionUpdateTimer.Stop();
			selectedHistoryIndexSet.Clear();
			clearSelectionText();
			syncHistoryListSelectionFromSet();
			updateControlStates();
			historyListBox.Focus();
		};
		filterUpdateTimer.Tick += (sender, eventArgs) => {
			runFilter();
		};
		filterTextBox.TextChanged += (sender, eventArgs) => {
			if (isFilterRunning)
			{
				return;
			}

			filterUpdateTimer.Stop();
			filterTextBox.BackColor = SystemColors.Window;
			filterUpdateTimer.Start();
		};
		separatorTextBox.TextChanged += (sender, eventArgs) => {
			updateSeparatorTextState();
		};
		sourceSelectorComboBox.SelectedIndexChanged += (sender, eventArgs) => {
			currentSource = (ClipboardManagerSource)sourceSelectorComboBox.SelectedIndex;
			applyCurrentSource();
		};
		combineHistoryForm.FormClosing += (sender, eventArgs) => {
			cancelActiveFilter();
		};

		controlsLayout.TabIndex = 0;
		selectionTextBox.TabIndex = 0;
		filterTextBox.TabIndex = 1;
		selectionButtonRow.TabIndex = 2;
		resetSelectionButton.TabIndex = 0;
		restoreValidSelectionButton.TabIndex = 1;
		modeToggleButton.TabIndex = 3;
		modeAffixTextBox.TabIndex = 4;
		separatorTextBox.TabIndex = 5;
		dialogFooterLayout.TabIndex = 1;
		sourceSelectorComboBox.TabIndex = 0;
		dialogActionButtonRow.TabIndex = 1;
		okButton.TabIndex = 0;
		cancelButton.TabIndex = 1;

		Control[] enterNavigationOrder = new Control[] { selectionTextBox, filterTextBox, resetSelectionButton, restoreValidSelectionButton, modeToggleButton, modeAffixTextBox, separatorTextBox, sourceSelectorComboBox, okButton, cancelButton };
		combineHistoryForm.KeyPreview = true;
		combineHistoryForm.KeyDown += (sender, eventArgs) => {
			if (eventArgs.KeyCode == Keys.Enter && eventArgs.Control)
			{
				eventArgs.SuppressKeyPress = true;
				eventArgs.Handled = true;
				if (okButton.Enabled)
				{
					okButton.PerformClick();
				}
				return;
			}
			if (eventArgs.KeyCode == Keys.Escape && !eventArgs.Alt && !eventArgs.Control && !eventArgs.Shift)
			{
				eventArgs.SuppressKeyPress = true;
				eventArgs.Handled = true;
				cancelButton.PerformClick();
				return;
			}
			if (eventArgs.KeyCode != Keys.Enter || eventArgs.Alt || eventArgs.Control || eventArgs.Shift)
			{
				return;
			}

			Control focusedControl = GetFocusedControl(combineHistoryForm);
			if (focusedControl == sourceSelectorComboBox && sourceSelectorComboBox.DroppedDown)
			{
				return;
			}
			if (focusedControl == modeToggleButton)
			{
				modeToggleButton.PerformClick();
				eventArgs.SuppressKeyPress = true;
				eventArgs.Handled = true;
				focusControl(modeAffixTextBox);
				return;
			}

			int focusedControlIndex = Array.IndexOf(enterNavigationOrder, focusedControl);
			if (focusedControlIndex >= 0)
			{
				eventArgs.SuppressKeyPress = true;
				eventArgs.Handled = true;
				for (int navigationOffset = 1; navigationOffset <= enterNavigationOrder.Length; navigationOffset++)
				{
					Control nextControl = enterNavigationOrder[(focusedControlIndex + navigationOffset) % enterNavigationOrder.Length];
					if (nextControl.Enabled && nextControl.Visible && nextControl.TabStop)
					{
						focusControl(nextControl);
						break;
					}
				}
			}
		};

		combineHistoryForm.CancelButton = cancelButton;
		combineHistoryForm.Controls.Add(historyPanel);
		combineHistoryForm.Controls.Add(divider);
		combineHistoryForm.Controls.Add(controlsPanel);
		combineHistoryForm.PerformLayout();
		controlsPanel.PerformLayout();
		controlsLayout.PerformLayout();
		dialogFooterLayout.PerformLayout();
		updateControlsPanelMinimumWidth();

		historyListBox.SelectedIndexChanged += (sender, eventArgs) => {
			updateSelectionTextFromList();
		};
		updateSeparatorTextState();
		updateControlStates();
		focusControl(selectionTextBox);

		DialogResult dialogResult = combineHistoryForm.ShowDialog();
		SaveRememberedClipboardManagerSource(currentSource);

		if (dialogResult != DialogResult.OK)
		{
			return null;
		}

		var selectedHistoryIndices = selectedHistoryIndexSet.OrderBy(selectionIndex => selectionIndex).ToList();
		string itemAffixText = modeAffixTextBox.Text;
		string separatorText;
		if (!TryUnescapeSeparatorText(separatorTextBox.Text, out separatorText))
		{
			return null;
		}
		var outputBuilder = new StringBuilder();
		for (int selectedItemPosition = 0; selectedItemPosition < selectedHistoryIndices.Count; selectedItemPosition++)
		{
			if (selectedItemPosition > 0)
			{
				outputBuilder.Append(separatorText);
			}
			if (isNumberedMode)
			{
				outputBuilder.Append(selectedItemPosition + 1).Append(itemAffixText);
			}
			else
			{
				outputBuilder.Append(itemAffixText);
			}
			outputBuilder.Append(historyItems[selectedHistoryIndices[selectedItemPosition]].Trim());
		}

		string combinedResult = outputBuilder.ToString();
		BFS.Clipboard.SetText(combinedResult);
		return combinedResult;
	}
}