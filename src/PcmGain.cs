using System;
using NAudio.Wave;

namespace MultiAudioOutput
{
    /// <summary>
    /// 采样级增益。
    ///
    /// 为什么要自己算：NAudio 的 VolumeSampleProvider 只能接在 ISampleProvider 链上，
    /// 而本程序的每个输出端是 BufferedWaveProvider（字节缓冲）。改成采样提供者链需要
    /// 把「一份采集数据广播给多个输出」改成「每路各自转换」，涉及格式协商与重采样，
    /// 风险远大于收益。这里直接对字节缓冲做增益，保持现有广播结构不变。
    ///
    /// 支持 8/16/24/32 位 PCM 与 32/64 位 IEEE float；遇到不认识的格式**原样写回**，
    /// 绝不猜——猜错会把声音变成噪声，而静音/失真比音量没变糟糕得多。
    /// </summary>
    internal static class PcmGain
    {
        /// <summary>把 gain 应用到一块字节缓冲上（原地修改）。</summary>
        /// <param name="buffer">样本数据</param>
        /// <param name="offset">起始字节</param>
        /// <param name="count">字节数</param>
        /// <param name="format">采集格式</param>
        /// <param name="gain">线性增益，0 = 静音，1 = 原样</param>
        internal static void Apply(byte[] buffer, int offset, int count, WaveFormat format, float gain)
        {
            if (buffer == null || count <= 0 || format == null) { return; }
            if (offset < 0 || offset + count > buffer.Length) { return; }
            if (gain < 0f) { gain = 0f; }
            if (gain > 8f) { gain = 8f; }

            // 1.0 走原样返回：默认音量下必须逐字节不变，任何舍入都不应发生。
            if (gain == 1f) { return; }

            // 0.0 直接清零整块字节。
            // 不能交给下面的逐样本相乘：浮点样本乘 0 会得到 -0.0（位模式 0x80000000），
            // 数值上等于静音，但字节不是零——「音量拖到 0」应当保证数字静音，
            // 而下游重采样/混音对 -0.0 与 +0.0 的处理并不保证一致。
            // 顺带这也是最快的一条路径（Array.Clear 走 memset）。
            if (gain == 0f) { Array.Clear(buffer, offset, count); return; }

            WaveFormatEncoding enc = format.Encoding;
            int bits = format.BitsPerSample;

            // Extensible 的真实编码藏在 SubFormat 里（本机采集端就是这种：
            // Encoding=Extensible、Bits=32、SubFormat=IEEE_FLOAT）。
            WaveFormatExtensible ext = format as WaveFormatExtensible;
            if (enc == WaveFormatEncoding.Extensible && ext != null)
            {
                enc = SubFormatEncoding(ext.SubFormat);
            }

            if (enc == WaveFormatEncoding.IeeeFloat)
            {
                if (bits == 32) { ApplyFloat32(buffer, offset, count, gain); return; }
                if (bits == 64) { ApplyFloat64(buffer, offset, count, gain); return; }
                return; // 未知位宽：原样保留
            }

            if (enc == WaveFormatEncoding.Pcm)
            {
                switch (bits)
                {
                    case 8: ApplyPcm8(buffer, offset, count, gain); return;
                    case 16: ApplyPcm16(buffer, offset, count, gain); return;
                    case 24: ApplyPcm24(buffer, offset, count, gain); return;
                    case 32: ApplyPcm32(buffer, offset, count, gain); return;
                    default: return;
                }
            }

            // 其它编码（压缩格式等）不处理
        }

        /// <summary>
        /// SubFormat GUID 转编码。只认 PCM 与 IEEE_FLOAT 两种，
        /// 其余返回 WaveFormatEncoding.Unknown，交给调用方原样放过。
        /// </summary>
        static WaveFormatEncoding SubFormatEncoding(Guid sub)
        {
            string s = sub.ToString();
            // 00000001-0000-0010-8000-00aa00389b71 = KSDATAFORMAT_SUBTYPE_PCM
            if (s.StartsWith("00000001-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase))
            {
                return WaveFormatEncoding.Pcm;
            }
            // 00000003-0000-0010-8000-00aa00389b71 = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            if (s.StartsWith("00000003-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase))
            {
                return WaveFormatEncoding.IeeeFloat;
            }
            return WaveFormatEncoding.Unknown;
        }

        static void ApplyFloat32(byte[] b, int off, int count, float gain)
        {
            int end = off + (count & ~3);
            for (int i = off; i < end; i += 4)
            {
                float f = BitConverter.ToSingle(b, i);
                // 采集端偶尔会给出 NaN/Inf（设备驱动异常），乘完会污染整条链路
                if (float.IsNaN(f) || float.IsInfinity(f)) { b[i] = 0; b[i + 1] = 0; b[i + 2] = 0; b[i + 3] = 0; continue; }
                f *= gain;
                if (f > 1f) { f = 1f; } else if (f < -1f) { f = -1f; }
                byte[] t = BitConverter.GetBytes(f);
                b[i] = t[0]; b[i + 1] = t[1]; b[i + 2] = t[2]; b[i + 3] = t[3];
            }
        }

        static void ApplyFloat64(byte[] b, int off, int count, float gain)
        {
            int end = off + (count & ~7);
            for (int i = off; i < end; i += 8)
            {
                double d = BitConverter.ToDouble(b, i);
                if (double.IsNaN(d) || double.IsInfinity(d)) { for (int k = 0; k < 8; k++) { b[i + k] = 0; } continue; }
                d *= gain;
                if (d > 1.0) { d = 1.0; } else if (d < -1.0) { d = -1.0; }
                byte[] t = BitConverter.GetBytes(d);
                for (int k = 0; k < 8; k++) { b[i + k] = t[k]; }
            }
        }

        static void ApplyPcm8(byte[] b, int off, int count, float gain)
        {
            // 8 位 PCM 是无符号，中点 128
            int end = off + count;
            for (int i = off; i < end; i++)
            {
                int v = (int)Math.Round((b[i] - 128) * gain) + 128;
                if (v < 0) { v = 0; } else if (v > 255) { v = 255; }
                b[i] = (byte)v;
            }
        }

        static void ApplyPcm16(byte[] b, int off, int count, float gain)
        {
            int end = off + (count & ~1);
            for (int i = off; i < end; i += 2)
            {
                int v = (short)(b[i] | (b[i + 1] << 8));
                int s = (int)Math.Round(v * gain);
                if (s > 32767) { s = 32767; } else if (s < -32768) { s = -32768; }
                b[i] = (byte)(s & 0xFF);
                b[i + 1] = (byte)((s >> 8) & 0xFF);
            }
        }

        static void ApplyPcm24(byte[] b, int off, int count, float gain)
        {
            int end = off + (count - count % 3);
            for (int i = off; i < end; i += 3)
            {
                int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
                if ((v & 0x800000) != 0) { v -= 0x1000000; }   // 符号扩展
                int s = (int)Math.Round(v * gain);
                if (s > 8388607) { s = 8388607; } else if (s < -8388608) { s = -8388608; }
                b[i] = (byte)(s & 0xFF);
                b[i + 1] = (byte)((s >> 8) & 0xFF);
                b[i + 2] = (byte)((s >> 16) & 0xFF);
            }
        }

        static void ApplyPcm32(byte[] b, int off, int count, float gain)
        {
            int end = off + (count & ~3);
            for (int i = off; i < end; i += 4)
            {
                int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
                // 用 double 中间值：int * float 在这个量级会丢精度，且极值处会溢出
                double s = Math.Round(v * (double)gain);
                if (s > int.MaxValue) { s = int.MaxValue; } else if (s < int.MinValue) { s = int.MinValue; }
                int n = (int)s;
                b[i] = (byte)(n & 0xFF);
                b[i + 1] = (byte)((n >> 8) & 0xFF);
                b[i + 2] = (byte)((n >> 16) & 0xFF);
                b[i + 3] = (byte)((n >> 24) & 0xFF);
            }
        }

        // ───────── 音量（百分比）→ 线性增益 ─────────

        /// <summary>
        /// 百分比音量换算成线性增益。
        ///
        /// 用平方曲线而不是线性：人耳对响度的感知接近对数，线性映射会让
        /// 「50%」听起来几乎和「100%」一样响，滑到低段又突然没声。
        /// 平方后 50% ≈ -12 dB、25% ≈ -24 dB，手感接近系统音量条。
        /// </summary>
        internal static float GainFromPercent(int percent)
        {
            if (percent <= 0) { return 0f; }
            if (percent >= 100) { return 1f; }
            float t = percent / 100f;
            return t * t;
        }
    }
}
