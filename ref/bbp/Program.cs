// Reads Owlcat blueprints-pack.bbp index and extracts blueprint blobs.
// Format (verified): uint32 count, then count * { 16-byte GUID, uint32 offset }, then blobs.
using System.Text;

string PACK = @"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\blueprints-pack.bbp";

var bytes = File.ReadAllBytes(PACK);
int count = BitConverter.ToInt32(bytes, 0);
Console.Error.WriteLine($"# entries={count} filesize={bytes.Length}");

var guids = new string[count];
var guidsNet = new string[count];
var offs = new uint[count];
for (int i = 0; i < count; i++)
{
    int p = 4 + i * 20;
    var g = new byte[16];
    Array.Copy(bytes, p, g, 0, 16);
    guids[i] = Convert.ToHexString(g).ToLowerInvariant();       // raw order
    guidsNet[i] = new Guid(g).ToString("N");                     // .NET mixed-endian
    offs[i] = BitConverter.ToUInt32(bytes, p + 16);
}

// blob end = next offset in ascending order
var order = Enumerable.Range(0, count).OrderBy(i => offs[i]).ToArray();
var endOf = new uint[count];
for (int k = 0; k < order.Length; k++)
    endOf[order[k]] = (k + 1 < order.Length) ? offs[order[k + 1]] : (uint)bytes.Length;

int Find(string want)
{
    want = want.Replace("-", "").ToLowerInvariant();
    for (int i = 0; i < count; i++)
        if (guids[i] == want || guidsNet[i] == want) return i;
    return -1;
}

// Extract printable ASCII runs from a blob
List<string> Strings(int idx, int minLen = 4)
{
    var res = new List<string>();
    var sb = new StringBuilder();
    for (uint p = offs[idx]; p < endOf[idx] && p < bytes.Length; p++)
    {
        byte b = bytes[p];
        if (b >= 0x20 && b < 0x7f) sb.Append((char)b);
        else { if (sb.Length >= minLen) res.Add(sb.ToString()); sb.Clear(); }
    }
    if (sb.Length >= minLen) res.Add(sb.ToString());
    return res;
}

string mode = args.Length > 0 ? args[0] : "help";

if (mode == "dump")
{
    int i = Find(args[1]);
    if (i < 0) { Console.WriteLine("NOT FOUND " + args[1]); return; }
    Console.WriteLine($"# guidRaw={guids[i]} guidNet={guidsNet[i]} off={offs[i]} len={endOf[i] - offs[i]}");
    foreach (var s in Strings(i, args.Length > 2 ? int.Parse(args[2]) : 4)) Console.WriteLine(s);
}
else if (mode == "refs")
{
    // print only 32-hex-char GUID-like tokens found inside the blob, with the name that precedes them
    int i = Find(args[1]);
    if (i < 0) { Console.WriteLine("NOT FOUND " + args[1]); return; }
    Console.WriteLine($"# blob len={endOf[i] - offs[i]}");
    foreach (var s in Strings(i, 32))
        foreach (var tok in ExtractGuids(s))
            Console.WriteLine(tok);
}
else if (mode == "hasref")
{
    // does blob <arg1> contain any of the guid strings arg2..n ?
    int i = Find(args[1]);
    if (i < 0) { Console.WriteLine("NOT FOUND " + args[1]); return; }
    var blob = Encoding.ASCII.GetString(bytes, (int)offs[i], (int)(endOf[i] - offs[i]));
    for (int a = 2; a < args.Length; a++)
    {
        var g = args[a].Replace("-", "").ToLowerInvariant();
        Console.WriteLine($"{g} : {(blob.Contains(g, StringComparison.OrdinalIgnoreCase) ? "PRESENT" : "absent")}");
    }
}
else if (mode == "whohas")
{
    // which blueprints contain this guid string (slow full scan)
    var needle = Encoding.ASCII.GetBytes(args[1].Replace("-", "").ToLowerInvariant());
    var hits = new List<int>();
    for (int i = 0; i < count; i++)
    {
        int len = (int)(endOf[i] - offs[i]);
        if (len <= 0 || offs[i] + len > bytes.Length) continue;
        if (IndexOf(bytes, (int)offs[i], len, needle) >= 0) hits.Add(i);
    }
    Console.WriteLine($"# {hits.Count} blueprints reference {args[1]}");
    foreach (var i in hits.Take(args.Length > 2 ? int.Parse(args[2]) : 40))
        Console.WriteLine($"{guidsNet[i]}  {NameOf(i)}");
}
else if (mode == "name")
{
    int i = Find(args[1]);
    if (i < 0) { Console.WriteLine("NOT FOUND"); return; }
    Console.WriteLine(NameOf(i));
}
else if (mode == "catalog")
{
    // guid <TAB> name  for every blueprint. Name is the identifier immediately preceding
    // the blob's own AssetId string near the end of the blob.
    var sw = new StreamWriter(args[1]);
    for (int i = 0; i < count; i++)
    {
        int len = (int)(endOf[i] - offs[i]);
        if (len <= 0 || offs[i] + len > bytes.Length) continue;
        var needle = Encoding.ASCII.GetBytes(guidsNet[i]);
        int at = LastIndexOf(bytes, (int)offs[i], len, needle);
        string nm = "";
        if (at > 0)
        {
            int e = at - 1;
            while (e >= offs[i] && (bytes[e] == 0x20)) e--;
            int s2 = e;
            while (s2 >= offs[i])
            {
                byte b = bytes[s2];
                bool idch = (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '_' || b == '.' || b == '-';
                if (!idch) break;
                s2--;
            }
            if (e > s2) nm = Encoding.ASCII.GetString(bytes, s2 + 1, e - s2);
        }
        sw.WriteLine(guidsNet[i] + "\t" + nm);
    }
    sw.Flush(); sw.Close();
    Console.WriteLine("written " + args[1]);
}
else if (mode == "findname")
{
    // find blueprints whose own name matches substring
    var pat = args[1];
    int shown = 0;
    for (int i = 0; i < count && shown < 60; i++)
    {
        var n = NameOf(i);
        if (n != null && n.Contains(pat, StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine($"{guidsNet[i]}  {n}"); shown++; }
    }
}

string NameOf(int idx)
{
    // heuristic: blueprint name is a printable run; take the longest run that looks like an identifier
    var ss = Strings(idx, 5);
    string best = null;
    foreach (var s in ss.Take(40))
    {
        var t = s.Trim();
        if (t.Length < 5) continue;
        if (t.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-') && !IsHex32(t))
        { if (best == null || t.Length > best.Length) best = t; }
    }
    return best;
}

static bool IsHex32(string s) => s.Length == 32 && s.All(Uri.IsHexDigit);

static IEnumerable<string> ExtractGuids(string s)
{
    for (int i = 0; i + 32 <= s.Length; i++)
    {
        bool ok = true;
        for (int j = 0; j < 32; j++) if (!Uri.IsHexDigit(s[i + j])) { ok = false; break; }
        if (ok) { yield return s.Substring(i, 32); i += 31; }
    }
}

static int IndexOf(byte[] hay, int start, int len, byte[] needle)
{
    int end = start + len - needle.Length;
    for (int i = start; i <= end; i++)
    {
        int j = 0;
        while (j < needle.Length && hay[i + j] == needle[j]) j++;
        if (j == needle.Length) return i;
    }
    return -1;
}

static int LastIndexOf(byte[] hay, int start, int len, byte[] needle)
{
    for (int i = start + len - needle.Length; i >= start; i--)
    {
        int j = 0;
        while (j < needle.Length && hay[i + j] == needle[j]) j++;
        if (j == needle.Length) return i;
    }
    return -1;
}
