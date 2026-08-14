using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MdViewer
{
    static class Program
    {
        /// <summary>
        /// Точка входа. Можно передать путь к .md файлу первым аргументом
        /// (тогда он откроется сразу).
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Все библиотеки лежат внутри exe (см. EmbedDependencies в csproj),
            // поэтому подключаем их до первого обращения к их типам.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            ExtractNativeLibraries();
            Run(args);
        }

        // Отдельный метод: иначе типы из встроенных библиотек понадобятся
        // ещё до того, как сработает обработчик выше.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1(args));
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var bytes = ReadResource(name);
            return bytes == null ? null : Assembly.Load(bytes);
        }

        private const string NativeDll = "WebView2Loader.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        /// <summary>
        /// WebView2Loader.dll — обычная (не .NET) библиотека, из ресурса её
        /// загрузить нельзя. Распаковываем в папку данных программы и грузим оттуда.
        /// </summary>
        private static void ExtractNativeLibraries()
        {
            var bytes = ReadResource(NativeDll);
            if (bytes == null) return;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MdViewer", "native",
                Assembly.GetExecutingAssembly().GetName().Version.ToString());
            var path = Path.Combine(dir, NativeDll);

            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, bytes);
                }
            }
            catch (IOException)
            {
                // файл занят уже запущенным экземпляром — он же и подойдёт
            }

            LoadLibrary(path);
        }

        private static byte[] ReadResource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                return bytes;
            }
        }
    }
}
