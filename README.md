# Combine Clipboard Manager Text Items with Filter, Numbering, and Separator Options

ClipboardFusion macro that opens a dialog for combining text items from Clipboard Manager History, Pinned, Online Recent, or Online Pinned. It supports .NET regex filtering, range-based selection, Prefix or Numbered output modes, and a custom separator, then writes the combined result back to the clipboard.

## Features

- Works with ClipboardFusion History, Pinned, Online Recent, and Online Pinned text items.
- Starts from the current Clipboard Manager list when launched from a real item list.
- Supports .NET regex filtering with default `IgnoreCase` and `Singleline` behavior.
- Lets you select items by clicking in the list or typing selection ranges such as `1-3, 5, 8-10`.
- Can switch between Prefix mode and Numbered mode using the same adjacent text field.
- Starts in Prefix mode, making it easy to wrap each selected item for formats such as JSON.
- Accepts escaped separator values like `\n`, `\r\n`, and `\t`.
- Returns the combined text and also places it on the clipboard.

## Usage

1. Add the contents of [src/main.cs](src/main.cs) to a ClipboardFusion C# macro.
2. Run the macro from ClipboardFusion.
3. Choose the source list if needed: `History`, `Pinned`, `Online Recent`, or `Online Pinned`.
4. Optionally enter a .NET regex filter to narrow the visible items.
5. Select entries from the list, or type a selection like `2, 4-6, 9` in the `Selection` box.
6. Leave the mode button on `Prefix`, or click it to switch to `Numbered`, then edit the adjacent text field.
7. Set the separator. The default is `\n` for one item per line.
8. Press `OK` to combine the selected items and copy the result to the clipboard.

## Notes

- If you launch the macro from a real Clipboard Manager list, it opens on that same source.
- If you launch it from a hotkey or the Macros tab, ClipboardFusion does not provide real list context, so the macro falls back to the last remembered source.
- You can force a startup source by calling the macro with an argument such as `cf-combine-source:pinned`.

Example wrapper macro:

```csharp
string textOut;
return BFS.ClipboardFusion.RunMacro(
    "Your Main Macro Name Here",
    "cf-combine-source:pinned",
    out textOut)
    ? textOut
    : null;
```

## Caveats

1. The selection parser is strict: duplicate or overlapping entries such as `1-5,3` are treated as invalid.
2. Invalid regex patterns or invalid separator escape sequences are rejected, and especially expensive regex patterns may time out.
3. Each selected item is trimmed before combining, so leading and trailing whitespace from individual entries is not preserved.
