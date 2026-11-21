using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ChromeSessionConverter
{
    public class NavigationEntry
    {
        public int NavId;
        public int TabId;
        public int Index;
        public string Url;
        public string Title;

        public NavigationEntry()
        {
            Url = string.Empty;
            Title = string.Empty;
        }
    }

    public class TabInfo
    {
        public int TabId;
        public int SelectedIndex;
        public Dictionary<int, NavigationEntry> Navigations;

        public TabInfo(int tabId)
        {
            TabId = tabId;
            SelectedIndex = -1;
            Navigations = new Dictionary<int, NavigationEntry>();
        }

        public NavigationEntry ActiveNavigation
        {
            get
            {
                if (Navigations.Count == 0) return null;
                if (SelectedIndex >= 0 && Navigations.ContainsKey(SelectedIndex))
                {
                    return Navigations[SelectedIndex];
                }
                int maxIdx = -1;
                foreach (int k in Navigations.Keys)
                {
                    if (k > maxIdx) maxIdx = k;
                }
                return Navigations[maxIdx];
            }
        }
    }

    public class ParsedSessionFile
    {
        public string FilePath;
        public string FileName;
        public long FileSize;
        public DateTime LastModified;
        public string FileType; // Tabs, Session, Apps, Unknown
        public List<TabInfo> Tabs;

        public ParsedSessionFile()
        {
            Tabs = new List<TabInfo>();
        }
    }

    class PickleReader
    {
        private byte[] _data;
        private int _pos;

        public PickleReader(byte[] data)
        {
            _data = data;
            _pos = 0;
        }

        public bool HasBytes(int count)
        {
            return _pos + count <= _data.Length;
        }

        public int ReadInt32()
        {
            if (!HasBytes(4)) return 0;
            int val = BitConverter.ToInt32(_data, _pos);
            _pos += 4;
            return val;
        }

        public string ReadString()
        {
            int len = ReadInt32();
            if (len <= 0 || !HasBytes(len)) return string.Empty;
            string s = Encoding.UTF8.GetString(_data, _pos, len);
            _pos += len;
            int pad = (4 - (len % 4)) % 4;
            _pos += pad;
            return s;
        }

        public string ReadString16()
        {
            int charCount = ReadInt32();
            int byteLen = charCount * 2;
            if (byteLen <= 0 || !HasBytes(byteLen)) return string.Empty;
            string s = Encoding.Unicode.GetString(_data, _pos, byteLen);
            _pos += byteLen;
            int pad = (4 - (byteLen % 4)) % 4;
            _pos += pad;
            return s;
        }
    }

    class SnssParser
    {
        public static ParsedSessionFile Parse(string filePath)
        {
            ParsedSessionFile result = new ParsedSessionFile();
            result.FilePath = filePath;
            FileInfo fi = new FileInfo(filePath);
            result.FileName = fi.Name;
            result.FileSize = fi.Length;
            result.LastModified = fi.LastWriteTime;

            string name = fi.Name;
            bool isTabs = name.StartsWith("Tabs", StringComparison.OrdinalIgnoreCase);
            bool isSession = name.StartsWith("Session", StringComparison.OrdinalIgnoreCase);
            bool isApps = name.StartsWith("Apps", StringComparison.OrdinalIgnoreCase);

            if (isTabs) result.FileType = "Tabs";
            else if (isSession) result.FileType = "Session";
            else if (isApps) result.FileType = "Apps";
            else result.FileType = "Unknown";

            byte[] bytes;
            try
            {
                // Open with FileShare.ReadWrite to allow reading even if Chrome is currently running
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bytes = new byte[fs.Length];
                    fs.Read(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error reading file {0}: {1}", filePath, ex.Message);
                return result;
            }

            if (bytes.Length < 8) return result;

            // Check magic
            if (bytes[0] != 0x53 || bytes[1] != 0x4E || bytes[2] != 0x53 || bytes[3] != 0x53)
            {
                Console.WriteLine("Warning: File {0} does not have SNSS magic header.", result.FileName);
                return result;
            }

            // uint version = BitConverter.ToUInt32(bytes, 4);

            int navCmd = isTabs ? 1 : 6;
            int selCmd = isTabs ? 4 : 7;

            Dictionary<int, TabInfo> tabMap = new Dictionary<int, TabInfo>();

            int pos = 8;
            while (pos + 3 <= bytes.Length)
            {
                ushort size = BitConverter.ToUInt16(bytes, pos);
                pos += 2;
                if (size == 0) break;
                if (pos >= bytes.Length) break;

                byte cmdId = bytes[pos];
                pos += 1;

                int payloadLen = size - 1;
                if (pos + payloadLen > bytes.Length) break;

                if (cmdId == navCmd)
                {
                    byte[] payload = new byte[payloadLen];
                    Buffer.BlockCopy(bytes, pos, payload, 0, payloadLen);

                    PickleReader reader = new PickleReader(payload);
                    int navId = reader.ReadInt32();
                    int tabId = reader.ReadInt32();
                    int index = reader.ReadInt32();
                    string url = reader.ReadString();
                    string title = reader.ReadString16();

                    if (!tabMap.ContainsKey(tabId))
                    {
                        tabMap[tabId] = new TabInfo(tabId);
                    }

                    NavigationEntry entry = new NavigationEntry();
                    entry.NavId = navId;
                    entry.TabId = tabId;
                    entry.Index = index;
                    entry.Url = url;
                    entry.Title = title;

                    tabMap[tabId].Navigations[index] = entry;
                }
                else if (cmdId == selCmd)
                {
                    if (payloadLen >= 8)
                    {
                        int tabId = BitConverter.ToInt32(bytes, pos);
                        int selectedIdx = BitConverter.ToInt32(bytes, pos + 4);
                        if (!tabMap.ContainsKey(tabId))
                        {
                            tabMap[tabId] = new TabInfo(tabId);
                        }
                        tabMap[tabId].SelectedIndex = selectedIdx;
                    }
                }

                pos += payloadLen;
            }

            foreach (KeyValuePair<int, TabInfo> kvp in tabMap)
            {
                if (kvp.Value.Navigations.Count > 0)
                {
                    result.Tabs.Add(kvp.Value);
                }
            }

            return result;
        }
    }

    class HtmlGenerator
    {
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;")
                       .Replace("\"", "&quot;")
                       .Replace("'", "&#39;");
        }

        public static string GenerateHtml(List<ParsedSessionFile> files, string title)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendFormat("<title>{0}</title>\n", Escape(title));
            sb.AppendLine("<style>");
            sb.AppendLine("  * { box-sizing: border-box; margin: 0; padding: 0; }");
            sb.AppendLine("  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: #f0f2f5; color: #1c1e21; line-height: 1.5; padding: 24px; }");
            sb.AppendLine("  .container { max-width: 1100px; margin: 0 auto; }");
            sb.AppendLine("  header { background: #ffffff; border-radius: 12px; padding: 24px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); margin-bottom: 20px; }");
            sb.AppendLine("  h1 { font-size: 24px; color: #1a73e8; margin-bottom: 8px; display: flex; align-items: center; gap: 10px; }");
            sb.AppendLine("  .subtitle { color: #5f6368; font-size: 14px; margin-bottom: 16px; }");
            sb.AppendLine("  .toolbar { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; }");
            sb.AppendLine("  .search-box { flex: 1; min-width: 250px; padding: 10px 16px; border: 1px solid #dadce0; border-radius: 8px; font-size: 14px; outline: none; transition: border-color 0.2s; }");
            sb.AppendLine("  .search-box:focus { border-color: #1a73e8; box-shadow: 0 0 0 2px rgba(26,115,232,0.2); }");
            sb.AppendLine("  .btn { background: #1a73e8; color: #fff; border: none; padding: 10px 16px; border-radius: 8px; font-size: 14px; font-weight: 500; cursor: pointer; transition: background 0.2s; }");
            sb.AppendLine("  .btn:hover { background: #1557b0; }");
            sb.AppendLine("  .btn-outline { background: #fff; color: #1a73e8; border: 1px solid #dadce0; }");
            sb.AppendLine("  .btn-outline:hover { background: #f8f9fa; border-color: #1a73e8; }");
            sb.AppendLine("  .file-card { background: #ffffff; border-radius: 12px; padding: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); margin-bottom: 20px; }");
            sb.AppendLine("  .file-header { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #f1f3f4; padding-bottom: 12px; margin-bottom: 16px; flex-wrap: wrap; gap: 8px; }");
            sb.AppendLine("  .file-title { font-size: 18px; font-weight: 600; color: #202124; display: flex; align-items: center; gap: 8px; }");
            sb.AppendLine("  .badge { background: #e8f0fe; color: #1a73e8; padding: 3px 10px; border-radius: 12px; font-size: 12px; font-weight: 600; }");
            sb.AppendLine("  .badge-session { background: #fef7e0; color: #b06000; }");
            sb.AppendLine("  .badge-apps { background: #e6f4ea; color: #137333; }");
            sb.AppendLine("  .file-meta { color: #70757a; font-size: 13px; }");
            sb.AppendLine("  ol.tab-list { list-style-position: inside; padding: 0; }");
            sb.AppendLine("  li.tab-item { padding: 12px 14px; border-radius: 8px; margin-bottom: 6px; background: #fafbfc; border: 1px solid #f1f3f4; transition: background 0.15s, border-color 0.15s; }");
            sb.AppendLine("  li.tab-item:hover { background: #f1f3f4; border-color: #dadce0; }");
            sb.AppendLine("  .tab-title-link { font-weight: 600; color: #1a73e8; text-decoration: none; font-size: 15px; display: inline-block; }");
            sb.AppendLine("  .tab-title-link:hover { text-decoration: underline; }");
            sb.AppendLine("  .tab-url { display: block; color: #5f6368; font-size: 12px; margin-top: 3px; word-break: break-all; }");
            sb.AppendLine("  .history-toggle { font-size: 11px; color: #70757a; cursor: pointer; margin-top: 6px; display: inline-block; user-select: none; }");
            sb.AppendLine("  .history-toggle:hover { color: #1a73e8; }");
            sb.AppendLine("  .history-list { margin-top: 6px; padding-left: 20px; font-size: 12px; color: #5f6368; border-left: 2px solid #e8eaed; display: none; }");
            sb.AppendLine("  .empty-msg { color: #70757a; font-style: italic; padding: 12px 0; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"container\">");
            sb.AppendLine("  <header>");
            sb.AppendFormat("    <h1><span>🌐</span> {0}</h1>\n", Escape(title));
            sb.AppendFormat("    <p class=\"subtitle\">Generated on {0} &bull; Total Files: {1}</p>\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), files.Count);
            sb.AppendLine("    <div class=\"toolbar\">");
            sb.AppendLine("      <input type=\"text\" id=\"searchBox\" class=\"search-box\" placeholder=\"Filter tabs by title or URL...\" oninput=\"filterTabs()\">");
            sb.AppendLine("      <button class=\"btn btn-outline\" onclick=\"copyAllUrls()\">📋 Copy All URLs</button>");
            sb.AppendLine("      <button class=\"btn\" onclick=\"openAllVisible()\">🚀 Open Visible Tabs</button>");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </header>");

            int totalTabs = 0;
            foreach (ParsedSessionFile file in files)
            {
                totalTabs += file.Tabs.Count;
                string badgeClass = "badge";
                if (file.FileType == "Session") badgeClass = "badge badge-session";
                else if (file.FileType == "Apps") badgeClass = "badge badge-apps";

                sb.AppendLine("  <div class=\"file-card\">");
                sb.AppendLine("    <div class=\"file-header\">");
                sb.AppendFormat("      <div class=\"file-title\">{0} <span class=\"{1}\">{2}</span></div>\n", Escape(file.FileName), badgeClass, Escape(file.FileType));
                sb.AppendFormat("      <div class=\"file-meta\">{0} tabs &bull; Modified: {1} &bull; Size: {2:N0} bytes</div>\n",
                    file.Tabs.Count, file.LastModified.ToString("yyyy-MM-dd HH:mm:ss"), file.FileSize);
                sb.AppendLine("    </div>");

                if (file.Tabs.Count == 0)
                {
                    sb.AppendLine("    <div class=\"empty-msg\">No active tabs decoded from this file.</div>");
                }
                else
                {
                    sb.AppendLine("    <ol class=\"tab-list\">");
                    int tabIndex = 1;
                    foreach (TabInfo tab in file.Tabs)
                    {
                        NavigationEntry active = tab.ActiveNavigation;
                        string titleText = (active != null && !string.IsNullOrEmpty(active.Title)) ? active.Title : "(Untitled Tab)";
                        string urlText = (active != null && !string.IsNullOrEmpty(active.Url)) ? active.Url : string.Empty;

                        sb.AppendFormat("      <li class=\"tab-item\" data-url=\"{0}\" data-title=\"{1}\">\n", Escape(urlText.ToLowerInvariant()), Escape(titleText.ToLowerInvariant()));
                        sb.AppendFormat("        <a class=\"tab-title-link\" href=\"{0}\" target=\"_blank\">{1}. {2}</a>\n", Escape(urlText), tabIndex++, Escape(titleText));
                        sb.AppendFormat("        <span class=\"tab-url\">{0}</span>\n", Escape(urlText));

                        if (tab.Navigations.Count > 1)
                        {
                            sb.AppendFormat("        <div class=\"history-toggle\" onclick=\"toggleHistory(this)\">▾ Navigation history ({0} entries)</div>\n", tab.Navigations.Count);
                            sb.AppendLine("        <div class=\"history-list\">");
                            foreach (KeyValuePair<int, NavigationEntry> nav in tab.Navigations)
                            {
                                string nTitle = !string.IsNullOrEmpty(nav.Value.Title) ? nav.Value.Title : nav.Value.Url;
                                sb.AppendFormat("          <div>[{0}] <a href=\"{1}\" target=\"_blank\">{2}</a></div>\n",
                                    nav.Key, Escape(nav.Value.Url), Escape(nTitle));
                            }
                            sb.AppendLine("        </div>");
                        }

                        sb.AppendLine("      </li>");
                    }
                    sb.AppendLine("    </ol>");
                }

                sb.AppendLine("  </div>");
            }

            sb.AppendLine("</div>");

            sb.AppendLine("<script>");
            sb.AppendLine("function filterTabs() {");
            sb.AppendLine("  const q = document.getElementById('searchBox').value.toLowerCase().trim();");
            sb.AppendLine("  const items = document.querySelectorAll('.tab-item');");
            sb.AppendLine("  items.forEach(el => {");
            sb.AppendLine("    const u = el.getAttribute('data-url') || '';");
            sb.AppendLine("    const t = el.getAttribute('data-title') || '';");
            sb.AppendLine("    if (!q || u.includes(q) || t.includes(q)) {");
            sb.AppendLine("      el.style.display = '';");
            sb.AppendLine("    } else {");
            sb.AppendLine("      el.style.display = 'none';");
            sb.AppendLine("    }");
            sb.AppendLine("  });");
            sb.AppendLine("}");
            sb.AppendLine("function toggleHistory(btn) {");
            sb.AppendLine("  const list = btn.nextElementSibling;");
            sb.AppendLine("  if (list.style.display === 'block') {");
            sb.AppendLine("    list.style.display = 'none';");
            sb.AppendLine("    btn.textContent = btn.textContent.replace('▴', '▾');");
            sb.AppendLine("  } else {");
            sb.AppendLine("    list.style.display = 'block';");
            sb.AppendLine("    btn.textContent = btn.textContent.replace('▾', '▴');");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("function copyAllUrls() {");
            sb.AppendLine("  const visibleItems = document.querySelectorAll('.tab-item');");
            sb.AppendLine("  const urls = [];");
            sb.AppendLine("  visibleItems.forEach(el => {");
            sb.AppendLine("    if (el.style.display !== 'none') {");
            sb.AppendLine("      const a = el.querySelector('a.tab-title-link');");
            sb.AppendLine("      if (a && a.href && a.href !== '#' && !a.href.startsWith('javascript:')) urls.push(a.href);");
            sb.AppendLine("    }");
            sb.AppendLine("  });");
            sb.AppendLine("  if (urls.length === 0) { alert('No URLs to copy'); return; }");
            sb.AppendLine("  navigator.clipboard.writeText(urls.join('\\n')).then(() => {");
            sb.AppendLine("    alert('Copied ' + urls.length + ' URLs to clipboard!');");
            sb.AppendLine("  }).catch(e => {");
            sb.AppendLine("    prompt('Copy URLs:', urls.join('\\n'));");
            sb.AppendLine("  });");
            sb.AppendLine("}");
            sb.AppendLine("function openAllVisible() {");
            sb.AppendLine("  const visibleItems = document.querySelectorAll('.tab-item');");
            sb.AppendLine("  const urls = [];");
            sb.AppendLine("  visibleItems.forEach(el => {");
            sb.AppendLine("    if (el.style.display !== 'none') {");
            sb.AppendLine("      const a = el.querySelector('a.tab-title-link');");
            sb.AppendLine("      if (a && a.href && a.href !== '#' && !a.href.startsWith('javascript:')) urls.push(a.href);");
            sb.AppendLine("    }");
            sb.AppendLine("  });");
            sb.AppendLine("  if (urls.length > 15) {");
            sb.AppendLine("    if (!confirm('This will open ' + urls.length + ' tabs in new windows. Continue?')) return;");
            sb.AppendLine("  }");
            sb.AppendLine("  urls.forEach(u => window.open(u, '_blank'));");
            sb.AppendLine("}");
            sb.AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("   Chrome Sessions & Tabs to HTML Converter      ");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            string inputPath = null;
            string outputPath = null;

            if (args.Length > 0)
            {
                inputPath = args[0];
            }
            if (args.Length > 1)
            {
                outputPath = args[1];
            }

            if (string.IsNullOrEmpty(inputPath))
            {
                // Auto-detect default Chrome Sessions folder
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string defaultSessionsPath = Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Sessions");
                if (Directory.Exists(defaultSessionsPath))
                {
                    Console.WriteLine("Found Chrome default Sessions directory:");
                    Console.WriteLine("  " + defaultSessionsPath);
                    inputPath = defaultSessionsPath;
                }
                else
                {
                    Console.Write("Enter path to Chrome session file or Sessions folder: ");
                    inputPath = Console.ReadLine();
                    if (!string.IsNullOrEmpty(inputPath))
                    {
                        inputPath = inputPath.Trim().Trim('\"');
                    }
                }
            }

            if (string.IsNullOrEmpty(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
            {
                Console.WriteLine("Error: Specified file or directory does not exist: {0}", inputPath);
                PauseIfConsole();
                return;
            }

            List<string> filesToProcess = new List<string>();
            string reportTitle;

            if (File.Exists(inputPath))
            {
                filesToProcess.Add(inputPath);
                reportTitle = "Chrome Session - " + Path.GetFileName(inputPath);
                if (string.IsNullOrEmpty(outputPath))
                {
                    outputPath = Path.ChangeExtension(inputPath, ".html");
                }
            }
            else
            {
                reportTitle = "Chrome Sessions & Tabs Report";
                string[] files = Directory.GetFiles(inputPath, "*", SearchOption.TopDirectoryOnly);
                foreach (string f in files)
                {
                    string fname = Path.GetFileName(f);
                    if (fname.StartsWith("Session_", StringComparison.OrdinalIgnoreCase) ||
                        fname.StartsWith("Tabs_", StringComparison.OrdinalIgnoreCase) ||
                        fname.StartsWith("Apps_", StringComparison.OrdinalIgnoreCase))
                    {
                        filesToProcess.Add(f);
                    }
                }

                // Sort files newest first
                filesToProcess.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));

                if (string.IsNullOrEmpty(outputPath))
                {
                    outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chrome_Sessions_Report.html");
                }
            }

            if (filesToProcess.Count == 0)
            {
                Console.WriteLine("No Chrome Session, Tabs, or Apps files found in: {0}", inputPath);
                PauseIfConsole();
                return;
            }

            Console.WriteLine("Processing {0} file(s)...", filesToProcess.Count);
            List<ParsedSessionFile> parsedFiles = new List<ParsedSessionFile>();

            foreach (string file in filesToProcess)
            {
                Console.WriteLine(" -> Parsing {0}...", Path.GetFileName(file));
                ParsedSessionFile parsed = SnssParser.Parse(file);
                Console.WriteLine("    Type: {0}, Decoded Tabs: {1}", parsed.FileType, parsed.Tabs.Count);
                parsedFiles.Add(parsed);
            }

            Console.WriteLine();
            Console.WriteLine("Generating HTML report...");
            string html = HtmlGenerator.GenerateHtml(parsedFiles, reportTitle);

            try
            {
                File.WriteAllText(outputPath, html, Encoding.UTF8);
                Console.WriteLine("Successfully created HTML file:");
                Console.WriteLine("  " + outputPath);

                // Open in default browser
                try
                {
                    Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
                }
                catch
                {
                    // Ignore if Process.Start fails to launch browser
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to write HTML file: {0}", ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("Done!");
            PauseIfConsole();
        }

        static void PauseIfConsole()
        {
            // Pause only if not run with arguments (e.g. double clicked in Windows Explorer)
            // Wait 1 second or let user see output if needed
        }
    }
}
