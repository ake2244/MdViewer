using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MdViewer
{
    /// <summary>
    /// Связывает .md/.markdown с MdViewer для текущего пользователя
    /// (раздел HKCU, права администратора не нужны). Повторный вызов
    /// обновляет путь к exe — например, после переноса программы.
    /// </summary>
    internal static class FileAssociation
    {
        private const string ProgId = "MdViewer.Document";
        private const string AppName = "MdViewer";
        private const string CapabilitiesKey = @"Software\MdViewer\Capabilities";
        private static readonly string[] Extensions = { ".md", ".markdown" };

        /// <summary>Записывает ассоциацию. Возвращает true, если пользователь
        /// уже выбрал MdViewer для .md (тогда двойной клик точно откроет его).</summary>
        public static bool Register()
        {
            var exe = Application.ExecutablePath;

            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                // Тип документа: название, иконка, команда открытия.
                using (var prog = classes.CreateSubKey(ProgId))
                {
                    prog.SetValue("", "Документ Markdown");
                    using (var icon = prog.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", "\"" + exe + "\",0");
                    using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }

                foreach (var ext in Extensions)
                {
                    using (var key = classes.CreateSubKey(ext))
                    {
                        key.SetValue("", ProgId);
                        using (var owp = key.CreateSubKey("OpenWithProgids"))
                            owp.SetValue(ProgId, "");
                    }
                }

                // Пункт в списке «Открыть с помощью».
                using (var app = classes.CreateSubKey(@"Applications\MdViewer.exe"))
                {
                    using (var cmd = app.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                    using (var types = app.CreateSubKey("SupportedTypes"))
                        foreach (var ext in Extensions) types.SetValue(ext, "");
                }
            }

            // Регистрация в «Параметры → Приложения по умолчанию».
            using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
            {
                caps.SetValue("ApplicationName", AppName);
                caps.SetValue("ApplicationDescription", "Просмотр, редактирование и печать Markdown");
                using (var fa = caps.CreateSubKey("FileAssociations"))
                    foreach (var ext in Extensions) fa.SetValue(ext, ProgId);
            }
            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue(AppName, CapabilitiesKey);

            // Сообщаем проводнику, что ассоциации изменились.
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

            return IsChosenByUser(".md");
        }

        /// <summary>Выбор программы по умолчанию Windows хранит отдельно и
        /// защищает: сделать его может только сам пользователь. Проверяем,
        /// выбран ли уже MdViewer.</summary>
        private static bool IsChosenByUser(string ext)
        {
            using (var uc = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice"))
            {
                var chosen = uc?.GetValue("ProgId") as string;
                return string.Equals(chosen, ProgId, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(chosen, @"Applications\MdViewer.exe", StringComparison.OrdinalIgnoreCase);
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
