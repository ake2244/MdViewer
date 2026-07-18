using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MdViewer
{
    public class Form1 : Form
    {
        private readonly WebView2 _web = new WebView2();
        private readonly TextBox _editor = new TextBox();
        private readonly SplitContainer _split = new SplitContainer();
        private readonly Timer _previewTimer = new Timer { Interval = 400 };
        private readonly ToolStripMenuItem _miSave;
        private readonly ToolStripMenuItem _miSaveAs;
        private readonly ToolStripMenuItem _miPrint;
        private readonly ToolStripMenuItem _miPdf;
        private readonly ToolStripMenuItem _miEditMode;
        private readonly string[] _startupArgs;
        private string _mdPath;
        private bool _webReady;
        private bool _dirty;
        private bool _loadingText;
        private bool _unixLineEndings;

        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        public Form1(string[] args)
        {
            _startupArgs = args ?? new string[0];

            Text = "MdViewer";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { /* без иконки тоже работаем */ }
            Width = 1000;
            Height = 750;
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            KeyPreview = true;

            // --- стандартное меню ---
            var miOpen = new ToolStripMenuItem("Открыть…", null, (s, e) => OpenViaDialog())
            { ShortcutKeys = Keys.Control | Keys.O };
            _miSave = new ToolStripMenuItem("Сохранить", null, (s, e) => Save())
            { ShortcutKeys = Keys.Control | Keys.S, Enabled = false };
            _miSaveAs = new ToolStripMenuItem("Сохранить как…", null, (s, e) => SaveAs())
            { Enabled = false };
            _miPrint = new ToolStripMenuItem("Печать…", null, async (s, e) => await PrintAsync())
            { Enabled = false };
            _miPdf = new ToolStripMenuItem("Сохранить в PDF…", null, async (s, e) => await SavePdfAsync())
            { Enabled = false };
            var miExit = new ToolStripMenuItem("Выход", null, (s, e) => Close());

            var mFile = new ToolStripMenuItem("Файл");
            mFile.DropDownItems.AddRange(new ToolStripItem[]
            {
                miOpen, new ToolStripSeparator(),
                _miSave, _miSaveAs, new ToolStripSeparator(),
                _miPrint, _miPdf, new ToolStripSeparator(),
                miExit
            });

            _miEditMode = new ToolStripMenuItem("Режим редактирования", null,
                async (s, e) => await ToggleEditAsync()) { ShortcutKeys = Keys.Control | Keys.E };
            // Горячие клавиши Ctrl+Z/X/C/V обрабатывает само текстовое поле,
            // поэтому у пунктов меню — только подписи, без перехвата клавиш.
            var miUndo = new ToolStripMenuItem("Отменить", null, (s, e) => _editor.Undo())
            { ShortcutKeyDisplayString = "Ctrl+Z" };
            var miCut = new ToolStripMenuItem("Вырезать", null, (s, e) => _editor.Cut())
            { ShortcutKeyDisplayString = "Ctrl+X" };
            var miCopy = new ToolStripMenuItem("Копировать", null, (s, e) => _editor.Copy())
            { ShortcutKeyDisplayString = "Ctrl+C" };
            var miPaste = new ToolStripMenuItem("Вставить", null, (s, e) => _editor.Paste())
            { ShortcutKeyDisplayString = "Ctrl+V" };
            var miSelectAll = new ToolStripMenuItem("Выделить всё", null, (s, e) => _editor.SelectAll())
            { ShortcutKeyDisplayString = "Ctrl+A" };

            var mEdit = new ToolStripMenuItem("Правка");
            mEdit.DropDownItems.AddRange(new ToolStripItem[]
            {
                _miEditMode, new ToolStripSeparator(),
                miUndo, new ToolStripSeparator(),
                miCut, miCopy, miPaste, miSelectAll
            });
            mEdit.DropDownOpening += (s, e) =>
            {
                var editing = InEditMode;
                _miEditMode.Checked = editing;
                _miEditMode.Enabled = _webReady;
                miUndo.Enabled = editing && _editor.CanUndo;
                miCut.Enabled = editing && _editor.SelectionLength > 0;
                miCopy.Enabled = editing && _editor.SelectionLength > 0;
                miPaste.Enabled = editing && Clipboard.ContainsText();
                miSelectAll.Enabled = editing;
            };

            var miHelp = new ToolStripMenuItem("Справка", null, (s, e) => ShowHelp())
            { ShortcutKeys = Keys.F1 };
            var miAbout = new ToolStripMenuItem("О программе", null, (s, e) => ShowAbout());

            var mHelp = new ToolStripMenuItem("Справка");
            mHelp.DropDownItems.AddRange(new ToolStripItem[] { miHelp, miAbout });

            var menu = new MenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { mFile, mEdit, mHelp });
            MainMenuStrip = menu;

            // Простое текстовое поле «как в блокноте»; стандартные
            // Ctrl+C/V/X/Z/A и контекстное меню даёт сама Windows.
            _editor.Multiline = true;
            _editor.WordWrap = true;
            _editor.ScrollBars = ScrollBars.Vertical;
            _editor.AcceptsTab = true;
            _editor.AcceptsReturn = true;
            _editor.MaxLength = 0;
            _editor.Font = new Font("Consolas", 12f);
            _editor.Dock = DockStyle.Fill;
            _editor.TextChanged += (s, e) =>
            {
                if (_loadingText) return;
                SetDirty(true);
                // живой предпросмотр: перерисовываем через паузу после набора
                if (InEditMode) { _previewTimer.Stop(); _previewTimer.Start(); }
            };

            _previewTimer.Tick += async (s, e) =>
            {
                _previewTimer.Stop();
                if (_webReady) await RenderAsync();
            };

            // Слева редактор, справа предпросмотр; в режиме просмотра
            // левая панель спрятана.
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;
            _split.Panel1Collapsed = true;
            _web.Dock = DockStyle.Fill;
            _split.Panel1.Controls.Add(_editor);
            _split.Panel2.Controls.Add(_web);

            Controls.Add(_split);
            Controls.Add(menu);

            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (s, e) =>
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                    LoadMarkdown(files[0]);
            };

            // Если колесо мыши пришло форме (фокус на панели кнопок),
            // прокручиваем страницу вручную.
            MouseWheel += (s, e) =>
            {
                if (_webReady && _web.Visible)
                    _web.CoreWebView2.ExecuteScriptAsync(
                        "window.scrollBy(0, " + (-e.Delta) + ");");
            };

            FormClosing += (s, e) =>
            {
                if (!_dirty) return;
                var r = MessageBox.Show(this,
                    "Есть несохранённые изменения. Сохранить их?",
                    "MdViewer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) e.Cancel = true;
                else if (r == DialogResult.Yes && !Save()) e.Cancel = true;
            };

            Shown += async (s, e) => await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                // Папка для служебных данных браузерного движка — в профиле
                // пользователя, чтобы программа могла лежать в любом месте.
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MdViewer");
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await _web.EnsureCoreWebView2Async(env);

                // Область просмотра занимает встроенный браузер, поэтому
                // перетаскивание файла ловим внутри него и передаём сюда,
                // иначе браузер откроет файл сам.
                await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
window.addEventListener('dragover', function (e) { e.preventDefault(); });
window.addEventListener('drop', function (e) {
    e.preventDefault();
    if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length)
        chrome.webview.postMessageWithAdditionalObjects('fileDrop', e.dataTransfer.files);
});");
                _web.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    if (e.AdditionalObjects != null && e.AdditionalObjects.Count > 0
                        && e.AdditionalObjects[0] is CoreWebView2File file)
                        LoadMarkdown(file.Path);
                };

                // После загрузки документа фокус — в область просмотра,
                // чтобы сразу работали колесо мыши и клавиши PgUp/PgDn.
                _web.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    // в режиме редактирования фокус не отбираем у редактора
                    if (_web.Visible && !InEditMode) _web.Focus();
                };

                // Мы просмотрщик, а не браузер: внешние ссылки уходят в
                // системный браузер, соседние md-файлы открываются здесь же.
                _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
                _web.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    OpenInSystemBrowser(e.Uri);
                };

                _webReady = true;

                if (_startupArgs.Length > 0 && File.Exists(_startupArgs[0]))
                    LoadMarkdown(_startupArgs[0]);
                else
                    ShowWelcome();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Не удалось запустить встроенный браузер (WebView2).\n\n" + ex.Message,
                    "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenViaDialog()
        {
            using (var dlg = new OpenFileDialog
            {
                Filter = "Markdown (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|Все файлы (*.*)|*.*",
                Title = "Открыть файл Markdown"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    LoadMarkdown(dlg.FileName);
            }
        }

        private void LoadMarkdown(string path)
        {
            if (!_webReady)
                return;

            if (_dirty)
            {
                var r = MessageBox.Show(this,
                    "Есть несохранённые изменения — они будут потеряны.\nПродолжить?",
                    "MdViewer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            string markdown;
            try
            {
                markdown = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть файл:\n" + ex.Message,
                    "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Текстовое поле Windows понимает только CRLF; запоминаем исходный
            // стиль концов строк, чтобы вернуть его при сохранении.
            _unixLineEndings = !markdown.Contains("\r\n") && markdown.Contains("\n");
            var normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n")
                                     .Replace("\n", "\r\n");

            _loadingText = true;
            _editor.Text = normalized;
            _editor.Select(0, 0);
            _loadingText = false;

            _mdPath = path;
            MapFolder();
            SetDirty(false);

            var ignored = RenderAsync();

            SetDocumentActionsEnabled(true);
        }

        private void SetDocumentActionsEnabled(bool on)
        {
            _miSave.Enabled = on;
            _miSaveAs.Enabled = on;
            _miPrint.Enabled = on;
            _miPdf.Enabled = on;
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var uri = e.Uri ?? "";

            // Наши собственные страницы (NavigateToString) — пропускаем.
            if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;

            // Относительная ссылка внутри документа: если это соседний
            // markdown-файл — открываем его в MdViewer.
            if (uri.StartsWith("https://md.local/", StringComparison.OrdinalIgnoreCase))
            {
                if (_mdPath == null) return;
                try
                {
                    var rel = Uri.UnescapeDataString(new Uri(uri).AbsolutePath)
                        .TrimStart('/').Replace('/', '\\');
                    var full = Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(_mdPath)), rel);
                    var ext = Path.GetExtension(full).ToLowerInvariant();
                    if (File.Exists(full) &&
                        (ext == ".md" || ext == ".markdown" || ext == ".txt"))
                        BeginInvoke((Action)(() => LoadMarkdown(full)));
                }
                catch { /* битая ссылка — просто ничего не делаем */ }
                return;
            }

            OpenInSystemBrowser(uri);
        }

        private static void OpenInSystemBrowser(string uri)
        {
            if (uri == null) return;
            if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;
            try { System.Diagnostics.Process.Start(uri); }
            catch { /* нет браузера по умолчанию — не падаем */ }
        }

        /// <summary>Отдаёт папку текущего файла встроенному браузеру под именем
        /// md.local, чтобы работали относительные картинки.</summary>
        private void MapFolder()
        {
            if (_mdPath == null) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(_mdPath));
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "md.local", dir, CoreWebView2HostResourceAccessKind.Allow);
        }

        /// <summary>Показывает текст из редактора в области просмотра и
        /// завершается, когда страница полностью загружена. Позиция прокрутки
        /// сохраняется — важно для живого предпросмотра при наборе.</summary>
        private async Task RenderAsync()
        {
            double scrollY = 0;
            try
            {
                var raw = await _web.CoreWebView2.ExecuteScriptAsync("window.scrollY");
                double.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out scrollY);
            }
            catch { /* страницы ещё нет — начнём с начала */ }

            var tcs = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = (s, e) =>
            {
                _web.CoreWebView2.NavigationCompleted -= handler;
                tcs.TrySetResult(true);
            };
            _web.CoreWebView2.NavigationCompleted += handler;

            var body = Markdown.ToHtml(_editor.Text ?? "", Pipeline);
            _web.NavigateToString(WrapHtml(body));
            await tcs.Task;

            if (scrollY > 0)
                await _web.CoreWebView2.ExecuteScriptAsync(
                    "window.scrollTo(0, " + scrollY.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + ");");
        }

        private bool InEditMode
        {
            get { return !_split.Panel1Collapsed; }
        }

        private async Task ToggleEditAsync()
        {
            if (!_webReady) return;

            if (!InEditMode)
            {
                // просмотр -> редактирование: слева текст, справа предпросмотр
                _split.Panel1Collapsed = false;
                try { _split.SplitterDistance = Math.Max(200, ClientSize.Width / 2); }
                catch { /* окно слишком узкое — оставляем как есть */ }
                SetDocumentActionsEnabled(true);
                if (_mdPath == null) SetDirty(_dirty); // обновить заголовок «Новый документ»
                _editor.Focus();
                _editor.Select(0, 0);
            }
            else
            {
                await ShowPreviewAsync();
            }
        }

        /// <summary>Переключает из редактирования в просмотр, дождавшись отрисовки.</summary>
        private async Task ShowPreviewAsync()
        {
            _previewTimer.Stop();
            await RenderAsync();
            _split.Panel1Collapsed = true;
            _web.Focus();
        }

        private void SaveAs()
        {
            var remembered = _mdPath;
            _mdPath = null;               // Save() спросит новое имя
            if (!Save()) _mdPath = remembered;
        }

        private bool Save()
        {
            var path = _mdPath;
            if (path == null)
            {
                using (var dlg = new SaveFileDialog
                {
                    Filter = "Markdown (*.md)|*.md|Текст (*.txt)|*.txt|Все файлы (*.*)|*.*",
                    FileName = "документ.md",
                    Title = "Сохранить файл"
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                    path = dlg.FileName;
                }
            }

            try
            {
                var text = _editor.Text;
                if (_unixLineEndings) text = text.Replace("\r\n", "\n");
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить файл:\n" + ex.Message,
                    "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            _mdPath = path;
            MapFolder();
            SetDirty(false);
            return true;
        }

        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            var name = _mdPath != null ? Path.GetFileName(_mdPath) : "Новый документ";
            Text = name + (dirty ? " *" : "") + " — MdViewer";
        }

        private async Task PrintAsync()
        {
            // В режиме редактирования сначала обновляем предпросмотр,
            // чтобы на печать ушёл актуальный текст.
            if (InEditMode)
            {
                _previewTimer.Stop();
                await RenderAsync();
            }

            // Стандартное окно печати браузера: предпросмотр, выбор принтера,
            // там же есть «Сохранить как PDF». Ctrl+P внутри окна тоже работает.
            _web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }

        private async Task SavePdfAsync()
        {
            if (InEditMode)
            {
                _previewTimer.Stop();
                await RenderAsync();
            }

            using (var dlg = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = (_mdPath != null
                    ? Path.GetFileNameWithoutExtension(_mdPath)
                    : "документ") + ".pdf",
                Title = "Сохранить в PDF"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                var ok = await _web.CoreWebView2.PrintToPdfAsync(dlg.FileName, null);
                if (ok)
                    MessageBox.Show(this, "PDF сохранён:\n" + dlg.FileName,
                        "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Не удалось сохранить PDF.",
                        "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Открытие файла:\n" +
                "  •  Файл → Открыть… (Ctrl+O), перетащить файл в окно\n" +
                "     или двойной клик по .md файлу в проводнике.\n\n" +
                "Редактирование (Ctrl+E):\n" +
                "  •  слева — текст как в блокноте, справа — живой предпросмотр;\n" +
                "  •  работают Ctrl+Z (отмена), Ctrl+X/C/V, Ctrl+A;\n" +
                "  •  границу между панелями можно двигать мышью;\n" +
                "  •  повторное нажатие Ctrl+E — возврат в просмотр.\n\n" +
                "Сохранение:\n" +
                "  •  Ctrl+S или Файл → Сохранить;\n" +
                "  •  звёздочка * в заголовке — есть несохранённые изменения.\n\n" +
                "Печать:\n" +
                "  •  Файл → Печать… — принтер или «Сохранить как PDF»;\n" +
                "  •  Файл → Сохранить в PDF… — сразу в PDF-файл.\n\n" +
                "Ссылки в документе:\n" +
                "  •  внешние открываются в вашем браузере;\n" +
                "  •  ссылки на соседние .md файлы — в этом окне.",
                "Справка — MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(this,
                "MdViewer " + Application.ProductVersion + "\n\n" +
                "Простой и лёгкий просмотрщик Markdown-файлов\n" +
                "с редактированием, печатью и экспортом в PDF.\n\n" +
                "Использует открытые компоненты:\n" +
                "  •  Markdig — преобразование Markdown в HTML\n" +
                "  •  Microsoft Edge WebView2 — отображение\n\n" +
                "Свободная программа с открытым исходным кодом (лицензия MIT).",
                "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowWelcome()
        {
            _web.NavigateToString(WrapHtml(
                "<div style='text-align:center; margin-top:120px; color:#57606a'>" +
                "<h1 style='border:none'>MdViewer</h1>" +
                "<p>Откройте файл Markdown через <b>Файл → Открыть…</b> (Ctrl+O)<br>" +
                "или просто перетащите его в это окно.</p>" +
                "<p><b>Правка → Режим редактирования</b> (Ctrl+E) — правка текста " +
                "как в блокноте, сохранение — Ctrl+S.</p>" +
                "<p>Печать и экспорт в PDF — в меню <b>Файл</b>.</p>" +
                "</div>"));
        }

        private static string WrapHtml(string body)
        {
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                   "<base href=\"https://md.local/\">" +
                   "<style>" + Css + "</style></head><body>" + body + "</body></html>";
        }

        // Оформление в духе GitHub + аккуратная версия для печати.
        private const string Css = @"
body {
    font-family: 'Segoe UI', -apple-system, Arial, sans-serif;
    font-size: 16px;
    line-height: 1.6;
    color: #1f2328;
    max-width: 860px;
    margin: 0 auto;
    padding: 24px 32px 48px;
}
h1, h2 { border-bottom: 1px solid #d8dee4; padding-bottom: .3em; }
h1, h2, h3, h4, h5, h6 { margin-top: 1.4em; margin-bottom: .5em; line-height: 1.25; }
a { color: #0969da; text-decoration: none; }
a:hover { text-decoration: underline; }
code {
    font-family: Consolas, 'Courier New', monospace;
    font-size: 85%;
    background: #f0f2f5;
    padding: .2em .4em;
    border-radius: 4px;
}
pre {
    background: #f6f8fa;
    padding: 12px 16px;
    border-radius: 6px;
    overflow-x: auto;
}
pre code { background: none; padding: 0; font-size: 85%; }
blockquote {
    margin: 0;
    padding: 0 1em;
    color: #57606a;
    border-left: 4px solid #d8dee4;
}
table { border-collapse: collapse; margin: 1em 0; }
th, td { border: 1px solid #d8dee4; padding: 6px 13px; }
th { background: #f6f8fa; }
tr:nth-child(2n) td { background: #fafbfc; }
img { max-width: 100%; }
hr { border: none; border-top: 1px solid #d8dee4; margin: 24px 0; }
ul, ol { padding-left: 2em; }
li + li { margin-top: .25em; }

@media print {
    body { max-width: none; padding: 0; font-size: 12pt; }
    pre, blockquote, table, img { page-break-inside: avoid; }
    h1, h2, h3 { page-break-after: avoid; }
    a { color: inherit; }
}
";
    }
}
