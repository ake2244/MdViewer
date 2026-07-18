using System;
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1(args));
        }
    }
}
