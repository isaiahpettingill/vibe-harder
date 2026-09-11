"""Create a disposable native-launch profile; never touches personal sessions."""
import pathlib
import sqlite3
import sys

profile = pathlib.Path(sys.argv[1]).resolve()
profile.mkdir(parents=True, exist_ok=False)
repo = pathlib.Path(__file__).resolve().parent.parent
with sqlite3.connect(profile / "sessions.db") as db:
    db.executescript("""
        CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,path TEXT NOT NULL,distro TEXT);
        CREATE TABLE chats(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL,session_id TEXT,title TEXT NOT NULL,updated TEXT NOT NULL,draft TEXT NOT NULL DEFAULT '',archived INTEGER DEFAULT 0,provider TEXT DEFAULT 'Codex',pending_input TEXT);
        CREATE TABLE messages(id TEXT PRIMARY KEY,chat_id TEXT NOT NULL,role TEXT NOT NULL,text TEXT NOT NULL,tool_id TEXT,seq INTEGER NOT NULL);
        CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE attachments(owner_id TEXT PRIMARY KEY,json TEXT NOT NULL);
    """)
    db.execute("INSERT INTO workspaces VALUES('smoke','Native AOT validation',?,NULL)", (str(profile),))
    db.execute("INSERT INTO chats(id,workspace_id,title,updated,draft) VALUES('chat','smoke','Markdown and persistence','2026-09-11T00:00:00Z','Recovered draft')")
    db.execute("INSERT INTO messages VALUES('message','chat','assistant',?,NULL,0)", (
        '# Native AOT\n\n**Bold**, [link](https://example.com), and code:\n\n```csharp\nConsole.WriteLine("Hello");\n```\n\n| One | Two |\n| --- | --- |\n| A | B |',))
    fixture = f'node "{repo / "tests/CodexManager.Tests/fake-acp.mjs"}"'
    for key in ('localCommand', 'Claude:localCommand', 'OpenCode:localCommand'):
        db.execute("INSERT INTO settings VALUES(?,?)", (key, fixture))
    db.execute("INSERT INTO settings VALUES('workspace','smoke')")
    db.execute("INSERT INTO settings VALUES('chat:smoke','chat')")
print(profile)
