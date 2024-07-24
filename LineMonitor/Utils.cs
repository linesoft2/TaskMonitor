using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LineMonitor
{
    internal class Utils
    {
        public static string ConvertBytesPerSecond(double bytesPerSecond)
        {
            // 定义转换系数
            const int scale = 1024;

            // 转换为KB/s
            double kilobytesPerSecond = bytesPerSecond / scale;
            if (kilobytesPerSecond < scale)
            {
                return kilobytesPerSecond.ToString("F2") + " KB/s";
            }

            // 转换为MB/s
            double megabytesPerSecond = kilobytesPerSecond / scale;
            if (megabytesPerSecond < scale)
            {
                return megabytesPerSecond.ToString("F2") + " MB/s";
            }

            // 转换为GB/s
            double gigabytesPerSecond = megabytesPerSecond / scale;
            return gigabytesPerSecond.ToString("F2") + " GB/s";
        }

    }
}
