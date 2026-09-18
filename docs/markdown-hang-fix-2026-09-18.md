# Saved-message startup hang (1.0.40)

## Cause

A copy of the affected desktop profile reproduced the unresponsive window. A managed stack capture placed the UI thread in `RegexInterpreter.TryMatchAtCurrentPosition`, called by `Markdown.Avalonia.Markdown.PrivateRunBlockGamut` during `ChatMarkdown.FlushRender` and transcript layout. Testing the upstream patterns with a bounded timeout isolated its unanchored table detector. Long non-table lines repeatedly scan the remaining text; restoring or realizing the same message repeats the stall. No database corruption or data reset was involved.

## Fix

`ChatMarkdownEngine` parses with the existing Markdig dependency and translates its AST directly into the existing selectable Avalonia document elements. Chat text no longer enters the Markdown.Avalonia regex parser. CommonMark paragraphs, headings, nested lists, blockquotes, rules, links, code, entities, pipe tables, strikethrough and task lists are supported. HTML is displayed as text. Code editors retain highlighting, wrapping and copy actions; incomplete streaming fences are rendered as code. Markdown.Avalonia remains for document controls, styles and asynchronous image loading only.

## Validation

- 53 focused Markdown, presentation, link, streaming, transcript scrolling/grouping, appearance and view-memory tests passed.
- New regressions cover 250,000-character lines with and without pipes, AST tables/alignment/escaped pipes, block structure, references, inline formatting, unfinished fences and raw HTML.
- Parsed all 2,717 user/assistant messages in the private profile copy in 183 ms total; slowest message 23 ms. Profile contents are not checked in.
- Both managed and Windows x64 Native AOT builds opened the copied profile with a responsive window.
- `UiTests.WorkspaceChatSwitchingDraftSearchAndMarkdownRender` fails before Markdown validation because its new-chat helper expects Claude but gets Codex. Reproduced separately with the original renderer restored; unrelated to this change.
- UI and test formatters were run on changed C# files. The full repository test suite was not run.

No saved messages, drafts, settings or recovery records need modification for this fix.
