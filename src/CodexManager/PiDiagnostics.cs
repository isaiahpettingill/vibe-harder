using System.Text;
using System.Text.Json;

namespace CodexManager;

public static class PiDiagnostics
{
    // pi-acp can return end_turn without forwarding Pi's error message. Read
    // only the current session's recent error, on the same host as the agent.
    public static async Task<string?> ReadError(Workspace workspace, string sessionId, DateTimeOffset since)
    {
        var script = "const sessionId=" + JsonSerializer.Serialize(sessionId, StoreJsonContext.Default.String) + ";const since=" + since.ToUnixTimeMilliseconds() + ";" + Probe;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        try
        {
            var output = await Hosts.Capture(Hosts.Agent(workspace, "node -e \"eval(Buffer.from('" + encoded + "','base64').toString())\""), TimeSpan.FromSeconds(15));
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch { return null; }
    }

    private const string Probe = """
        const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
        try {
          const map=JSON.parse(fs.readFileSync(path.join(os.homedir(),'.pi','pi-acp','session-map.json'),'utf8'));
          const file=map.sessions?.[sessionId]?.sessionFile;
          if(file){
            const fd=fs.openSync(file,'r');
            try {
              const size=fs.fstatSync(fd).size, length=Math.min(size,65536), buffer=Buffer.alloc(length);
              fs.readSync(fd,buffer,0,length,size-length);
              for(const line of buffer.toString('utf8').split('\n').reverse()){
                let row;try{row=JSON.parse(line)}catch{continue}
                const message=row.message;
                if(message?.role!=='assistant')continue;
                const time=typeof message.timestamp==='number'?message.timestamp:Date.parse(row.timestamp);
                if(!Number.isFinite(time)||time<since)break;
                if(message.stopReason==='error'&&typeof message.errorMessage==='string')process.stdout.write(message.errorMessage.slice(0,4000));
                break;
              }
            } finally {fs.closeSync(fd)}
          }
        }catch{}
        """;
}
