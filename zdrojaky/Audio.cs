using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Kocky
{
    /// <summary>
    /// All sound of the game, synthesised at start-up (no audio files): 24 short effects and a cosy café music loop,
    /// mixed by one background thread into a single waveOut stream (22050 Hz, mono, 16 bit) with a soft limiter.
    /// </summary>
    public static class Audio
    {
        public static volatile bool Muted, MusicOff, Hidden;

        const int SR = 22050, MaxVoices = 16, BufSamples = 256, BufCount = 8, WhdrDone = 1;
        // the loop is rendered at RMS 0.14 (peak ~0.65); this keeps it well under the effects (peaks 0.2-0.7)
        const float MusicLevel = 0.3f, Ceiling = 0.8f;
        const double TwoPi = 2 * Math.PI;

        static readonly string[] Names = { "pop", "clank", "swipe", "spit", "throw", "blast", "laser", "freeze", "zzz", "place", "upgrade", "sell", "round", "coins", "leak", "boss", "win", "lose", "meow", "click", "bite", "snipe", "web", "legend" };
        // 0 = frequent battle noise, 1 = actions and events, 2 = big moments; a new sound may only push out an equal or lower one
        static readonly int[] Priority = { 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 1, 1, 2, 1, 1, 2, 2, 2, 1, 0, 0, 0, 0, 2 };
        static readonly Dictionary<string, int> Ids = new Dictionary<string, int>();
        static readonly long[] lastPlay = new long[Names.Length], minGap = new long[Names.Length];
        static readonly object gate = new object();
        static readonly int[] pending = new int[64], taken = new int[64];
        static int pendingCount;

        static Thread thread;
        static volatile bool quit;
        static int device = -1;   // WAVE_MAPPER; the test harness sets a bogus id to act like a PC without sound

        sealed class Voice { public float[] Buf; public double Pos, Rate; public int Pri; public long Born; }

        // mixer state: sfx is ready before the device opens, music arrives from a worker a moment later
        static volatile float[][] sfx;
        static volatile float[] music;
        static readonly Voice[] voices = new Voice[MaxVoices];
        static readonly Random mixRnd = new Random(5);
        static float master, musicGain, env;
        static int musicPos;
        static long born;

        static Audio()
        {
            for (int i = 0; i < Names.Length; i++)
            {
                Ids[Names[i]] = i;
                minGap[i] = (long)((Names[i] == "pop" ? 0.035 : Names[i] == "laser" ? 0.09 : 0.05) * Stopwatch.Frequency);
                lastPlay[i] = long.MinValue / 2;
            }
            for (int k = 0; k < MaxVoices; k++) voices[k] = new Voice();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public short Tag, Channels; public int Rate, ByteRate; public short Align, Bits, Extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr { public IntPtr Data; public int Length, Recorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int device, ref WaveFormat fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        public static void Start()
        {
            if (thread != null) return;
            quit = false;
            thread = new Thread(Run) { IsBackground = true, Name = "zvuk", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public static void Stop()
        {
            var t = thread;
            if (t == null) return;
            quit = true;
            t.Join(1500);
            thread = null;
        }

        public static void Play(string name)
        {
            int id;
            if (name == null || Muted || Hidden || !Ids.TryGetValue(name, out id)) return;
            long now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (now - lastPlay[id] < minGap[id]) return;
                lastPlay[id] = now;
                if (pendingCount < pending.Length) pending[pendingCount++] = id;
            }
        }

        public static void Export(string dir)
        {
            Directory.CreateDirectory(dir);
            var fx = MakeSfx();
            for (int i = 0; i < Names.Length; i++) WriteWav(Path.Combine(dir, Names[i] + ".wav"), fx[i]);
            WriteWav(Path.Combine(dir, "music.wav"), MakeMusic());
        }

        static void WriteWav(string file, float[] x)
        {
            using (var w = new BinaryWriter(File.Create(file)))
            {
                int n = x.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(SR); w.Write(SR * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n);
                foreach (float v in x) w.Write((short)(Math.Max(-1f, Math.Min(1f, v)) * 32767));
            }
        }

        static void Run()
        {
            IntPtr h = IntPtr.Zero;
            var hdrs = new IntPtr[BufCount];
            var data = new IntPtr[BufCount];
            int hsize = Marshal.SizeOf(typeof(WaveHdr));
            try
            {
                if (sfx == null) sfx = MakeSfx();
                var fmt = new WaveFormat { Tag = 1, Channels = 1, Rate = SR, ByteRate = SR * 2, Align = 2, Bits = 16 };
                // flags 0 = no callback; finished buffers are found by polling WHDR_DONE
                if (waveOutOpen(out h, device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { h = IntPtr.Zero; return; }
                if (music == null)
                    new Thread(() =>
                    {
                        try { music = MakeMusic(); }
                        catch (Exception) { }   // shortcut: a failed render just means no music
                    }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
                int flagsAt = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");
                var tmp = new short[BufSamples];
                for (int i = 0; i < BufCount; i++)
                {
                    data[i] = Marshal.AllocHGlobal(BufSamples * 2);
                    hdrs[i] = Marshal.AllocHGlobal(hsize);
                    Marshal.StructureToPtr(new WaveHdr { Data = data[i], Length = BufSamples * 2 }, hdrs[i], false);
                    waveOutPrepareHeader(h, hdrs[i], hsize);
                    Marshal.WriteInt32(hdrs[i], flagsAt, Marshal.ReadInt32(hdrs[i], flagsAt) | WhdrDone);
                }
                while (!quit)
                {
                    for (int i = 0; i < BufCount; i++)
                    {
                        int flags = Marshal.ReadInt32(hdrs[i], flagsAt);
                        if ((flags & WhdrDone) == 0) continue;
                        Fill(tmp);
                        Marshal.Copy(tmp, 0, data[i], BufSamples);
                        Marshal.WriteInt32(hdrs[i], flagsAt, flags & ~WhdrDone);
                        waveOutWrite(h, hdrs[i], hsize);
                    }
                    Thread.Sleep(5);
                }
            }
            catch (Exception)
            {
                // shortcut: sound is a nice-to-have; any audio failure just means a quiet game
            }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    waveOutReset(h);
                    for (int i = 0; i < BufCount; i++) if (hdrs[i] != IntPtr.Zero) waveOutUnprepareHeader(h, hdrs[i], hsize);
                    waveOutClose(h);
                }
                for (int i = 0; i < BufCount; i++)
                {
                    if (hdrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(hdrs[i]);
                    if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
                }
            }
        }

        /// <summary>Mixes the next block: queued sounds start, the music loop and up to 16 voices are summed and limited.</summary>
        static void Fill(short[] buf)
        {
            int n;
            lock (gate)
            {
                n = pendingCount;
                Array.Copy(pending, taken, n);
                pendingCount = 0;
            }
            var fx = sfx;
            if (fx != null) for (int i = 0; i < n; i++) Launch(taken[i], fx);
            var mus = music;
            float masterTarget = Muted || Hidden ? 0 : 1, musicTarget = mus == null || MusicOff ? 0 : MusicLevel;
            float masterStep = 1f / (SR * 0.02f), musicStep = MusicLevel / (SR * 0.8f);
            float release = (float)Math.Exp(-1.0 / (SR * 0.15));
            for (int i = 0; i < buf.Length; i++)
            {
                master = Toward(master, masterTarget, masterStep);
                musicGain = Toward(musicGain, musicTarget, musicStep);
                float v = 0;
                if (mus != null)
                {
                    v = musicGain * mus[musicPos];
                    if (++musicPos >= mus.Length) musicPos = 0;
                }
                for (int k = 0; k < MaxVoices; k++)
                {
                    var vo = voices[k];
                    if (vo.Buf == null) continue;
                    int p = (int)vo.Pos;
                    if (p + 1 >= vo.Buf.Length) { vo.Buf = null; continue; }
                    float fr = (float)(vo.Pos - p);
                    v += vo.Buf[p] + (vo.Buf[p + 1] - vo.Buf[p]) * fr;
                    vo.Pos += vo.Rate;
                }
                v *= master;
                // soft limiter: instant attack, 150 ms release, so pile-ups duck smoothly instead of clipping
                env = Math.Max(Math.Abs(v), env * release);
                if (env > Ceiling) v *= Ceiling / env;
                buf[i] = (short)(v * 32767);
            }
        }

        static float Toward(float x, float target, float step)
        {
            return x < target ? Math.Min(target, x + step) : Math.Max(target, x - step);
        }

        /// <summary>Starts a sound on a free voice, or on the oldest voice that is not more important; otherwise drops it.</summary>
        static void Launch(int id, float[][] fx)
        {
            int pri = Priority[id], slot = -1;
            long oldest = long.MaxValue;
            for (int k = 0; k < MaxVoices; k++)
            {
                var vo = voices[k];
                if (vo.Buf == null) { slot = k; break; }
                if (vo.Pri <= pri && vo.Born < oldest) { oldest = vo.Born; slot = k; }
            }
            if (slot < 0) return;
            var v = voices[slot];
            v.Buf = fx[id];
            v.Pos = 0;
            v.Pri = pri;
            v.Born = ++born;
            // pops vary in pitch so a long chain of them does not sound like a machine gun
            v.Rate = id == 0 ? 0.88 + 0.24 * mixRnd.NextDouble() : pri == 0 ? 0.95 + 0.1 * mixRnd.NextDouble() : 1;
        }

        // ---------- sound effects ----------

        static float[][] MakeSfx()
        {
            var r = new Random(7);
            // same order as Names; the number is the loudness (RMS) each effect is levelled to.
            // New effects go at the end: they share r, so the older ones stay sample-identical.
            return new[]
            {
                Fit(Pop(r), 0.045), Fit(Clank(r), 0.05), Fit(Swipe(r), 0.06), Fit(Spit(r), 0.06), Fit(Throw(r), 0.05),
                Fit(Blast(r), 0.09), Fit(Laser(), 0.04), Fit(Freeze(r), 0.05), Fit(Zzz(r), 0.05), Fit(Chirp(), 0.08),
                Fit(Upgrade(), 0.07), Fit(Sell(r), 0.07), Fit(Jingle(), 0.09), Fit(Coins(), 0.06), Fit(Leak(), 0.09),
                Fit(Boss(r), 0.1), Fit(Win(), 0.1), Fit(Lose(), 0.1), Fit(Meow(), 0.1), Fit(Click(r), 0.05),
                Fit(Bite(r), 0.06), Fit(Snipe(r), 0.04), Fit(Web(), 0.05), Fit(Legend(), 0.1)
            };
        }

        /// <summary>Levels a sound to the given RMS (peak at most 0.85) and fades its last 4 ms to silence.</summary>
        static float[] Fit(float[] x, double rms)
        {
            double s = 0, peak = 0;
            foreach (float v in x) { s += v * v; peak = Math.Max(peak, Math.Abs(v)); }
            if (peak <= 0) return x;
            float g = (float)Math.Min(rms / Math.Sqrt(s / x.Length), 0.85 / peak);
            int fade = SR / 250;
            for (int i = 0; i < x.Length; i++) x[i] *= g * Math.Min(1f, (x.Length - 1 - i) / (float)fade);
            return x;
        }

        static float[] Gen(double sec, Func<double, double> f)
        {
            var x = new float[(int)(sec * SR)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)f(i / (double)SR);
            return x;
        }

        static double Noise(Random r) { return r.NextDouble() * 2 - 1; }

        /// <summary>One-pole low-pass coefficient for a cutoff in Hz.</summary>
        static double Lp(double cutoff) { return 1 - Math.Exp(-TwoPi * cutoff / SR); }

        static double Smooth(double x)
        {
            x = Math.Max(0, Math.Min(1, x));
            return x * x * (3 - 2 * x);
        }

        static double Hz(double midi) { return 440 * Math.Pow(2, (midi - 69) / 12.0); }

        /// <summary>A bell or coin strike u seconds ago: the tone plus an inharmonic partial that dies faster.</summary>
        static double Ting(double f, double u, double decay)
        {
            if (u < 0) return 0;
            return Math.Min(1, u / 0.002) * (Math.Sin(TwoPi * f * u) * Math.Exp(-u * decay) + 0.35 * Math.Sin(TwoPi * f * 2.76 * u) * Math.Exp(-u * decay * 2.2));
        }

        /// <summary>A brassy note started u seconds ago: harmonics that darken after the attack.</summary>
        static double Horn(double f, double u, double dur)
        {
            if (u < 0 || u > dur + 0.12) return 0;
            double env = Math.Min(1, u / 0.012) * (u > dur ? 1 - (u - dur) / 0.12 : 1) * (0.7 + 0.3 * Math.Exp(-u * 8));
            double bright = 0.3 + 0.45 * Math.Exp(-u * 7), w = 1, v = 0;
            for (int k = 1; k <= 8 && k * f < 9000; k++, w *= bright) v += w * Math.Sin(TwoPi * f * k * u);
            return v * env;
        }

        /// <summary>A sung tone: harmonics of pitch(t) shaped by two moving formants (the vowel). Meows, the chirp and the sad "wah"s.</summary>
        static float[] Voiced(double sec, Func<double, double> pitch, Func<double, double> f1, Func<double, double> f2, Func<double, double> amp, double tilt)
        {
            double ph = 0;
            return Gen(sec, t =>
            {
                double f = pitch(t), a = f1(t), b = f2(t), v = 0;
                ph += TwoPi * f / SR;
                for (int k = 1; k <= 30 && k * f < 9000; k++)
                {
                    double x = (k * f - a) / 300, y = (k * f - b) / 500;
                    v += (Math.Exp(-x * x) + 0.6 * Math.Exp(-y * y) + tilt / k) * Math.Sin(k * ph);
                }
                return v * amp(t);
            });
        }

        /// <summary>A vacuum layer bursts: a soft bubble plop rising in pitch and a puff of air.</summary>
        static float[] Pop(Random r)
        {
            double ph = 0, lp = 0;
            return Gen(0.11, t =>
            {
                ph += TwoPi * (320 + 720 * (1 - Math.Exp(-t * 55))) / SR;
                lp += 0.3 * (Noise(r) - lp);
                return Math.Min(1, t / 0.0015) * (Math.Sin(ph) * Math.Exp(-t * 45) + 0.5 * lp * Math.Exp(-t * 110));
            });
        }

        /// <summary>A claw glancing off armour: a short metallic tick.</summary>
        static float[] Clank(Random r)
        {
            double[] fr = { 1870, 2790, 4130, 5470, 7020 }, am = { 1, 0.7, 0.5, 0.35, 0.2 }, de = { 24, 30, 38, 48, 60 };
            return Gen(0.2, t =>
            {
                double v = t < 0.005 ? Noise(r) * (1 - t / 0.005) * 0.8 : 0;
                for (int i = 0; i < fr.Length; i++) v += am[i] * Math.Sin(TwoPi * fr[i] * t) * Math.Exp(-t * de[i]);
                return v;
            });
        }

        /// <summary>A quick airy scratch: a band of noise sliding up, rasped by the claw tips.</summary>
        static float[] Swipe(Random r)
        {
            double a = 0, b = 0;
            const double len = 0.14;
            return Gen(len, t =>
            {
                double u = t / len, n = Noise(r);
                a += Lp(1800 + 4500 * u) * (n - a);
                b += Lp(500 + 1500 * u) * (n - b);
                double env = u < 0.2 ? u / 0.2 : Math.Pow(1 - (u - 0.2) / 0.8, 2);
                return (a - b) * env * (0.6 + 0.4 * Math.Sin(TwoPi * (60 + 70 * u) * t));
            });
        }

        /// <summary>A hairball leaves: "p", a breathy "ff" and a short falling "tu".</summary>
        static float[] Spit(Random r)
        {
            double lp = 0, hp = 0, ph = 0;
            return Gen(0.16, t =>
            {
                lp += 0.55 * (Noise(r) - lp);
                hp += 0.12 * (lp - hp);
                double p = t < 0.012 ? Math.Sin(TwoPi * 150 * t) * Math.Sin(Math.PI * t / 0.012) : 0;
                double ff = t < 0.008 ? 0 : (lp - hp) * Math.Min(1, (t - 0.008) / 0.01) * Math.Exp(-(t - 0.008) * 20);
                ph += TwoPi * (430 - 130 * Math.Min(1, t / 0.08)) / SR;
                double tu = t < 0.008 ? 0 : Math.Sin(ph) * Math.Min(1, (t - 0.008) / 0.006) * Math.Exp(-(t - 0.008) * 38);
                return 0.8 * p + 1.1 * ff + 0.45 * tu;
            });
        }

        /// <summary>Whoosh: noise through a resonance that sweeps up and back down.</summary>
        static float[] Throw(Random r)
        {
            double lo = 0, bd = 0;
            const double len = 0.24;
            return Gen(len, t =>
            {
                double u = t / len, f = 2 * Math.Sin(Math.PI * (450 + 1300 * Math.Sin(Math.PI * u)) / SR);
                lo += f * bd;
                bd += f * (Noise(r) - lo - 0.6 * bd);
                double e = Math.Sin(Math.PI * u);
                return bd * e * e;
            });
        }

        /// <summary>A yarn ball bursts: a soft low thump and a fluffy puff.</summary>
        static float[] Blast(Random r)
        {
            double ph = 0, lp = 0;
            return Gen(0.42, t =>
            {
                ph += TwoPi * (46 + 110 * Math.Exp(-t * 18)) / SR;
                lp += Lp(250 + 2200 * Math.Exp(-t * 14)) * (Noise(r) - lp);
                return Math.Min(1, t / 0.004) * (Math.Sin(ph) * Math.Exp(-t * 9) + 0.9 * lp * Math.Exp(-t * 7));
            });
        }

        /// <summary>A thin zap falling in pitch.</summary>
        static float[] Laser()
        {
            double ph = 0;
            return Gen(0.12, t =>
            {
                ph += TwoPi * (750 + 2100 * Math.Exp(-t * 32)) / SR;
                return Math.Min(1, t / 0.002) * Math.Exp(-t * 22) * (Math.Sin(ph) + 0.25 * Math.Sin(3 * ph));
            });
        }

        /// <summary>Icy shimmer: high glassy partials entering one by one, with a crackle of frost.</summary>
        static float[] Freeze(Random r)
        {
            double[] fr = { 2093, 2637, 3136, 3520, 4186, 5274 };
            double prev = 0;
            return Gen(0.55, t =>
            {
                double v = 0;
                for (int i = 0; i < fr.Length; i++)
                {
                    double u = t - i * 0.03;
                    if (u > 0) v += Math.Sin(TwoPi * fr[i] * u) * Math.Min(1, u / 0.004) * Math.Exp(-u * 8) * (1 + 0.35 * Math.Sin(TwoPi * (9 + 4 * i) * u));
                }
                double n = Noise(r), h = n - prev;
                prev = n;
                return v + 0.4 * h * Math.Exp(-t * 16) * Math.Min(1, t / 0.003);
            });
        }

        /// <summary>A sleepy purr: a low hum and breath fluttering about 26 times a second, swelling in and out.</summary>
        static float[] Zzz(Random r)
        {
            double ph = 0, lp = 0;
            const double len = 0.42;
            return Gen(len, t =>
            {
                double u = t / len, e = Math.Sin(Math.PI * u), purr = 0.5 + 0.5 * Math.Sin(TwoPi * 26 * t);
                ph += TwoPi * (110 + 20 * u) / SR;
                lp += 0.1 * (Noise(r) - lp);
                double tone = Math.Sin(ph) + 0.6 * Math.Sin(2 * ph) + 0.35 * Math.Sin(3 * ph) + 0.2 * Math.Sin(5 * ph);
                return (0.5 * tone + 1.6 * lp) * purr * purr * e * e;
            });
        }

        /// <summary>A cat's friendly "mrrp" when placed: a rolled start gliding up.</summary>
        static float[] Chirp()
        {
            const double len = 0.24;
            return Voiced(len,
                t => 470 + 310 * Smooth(t / 0.14) - 40 * Smooth((t - 0.14) / 0.1),
                t => 500 + 400 * Smooth(t / 0.15),
                t => 1600 + 500 * Smooth(t / len),
                t => Math.Min(1, t / 0.012) * (1 - 0.45 * (1 - Math.Sin(TwoPi * 38 * t)) * Math.Max(0, 1 - t / 0.12)) * Math.Pow(Math.Min(1, (len - t) / 0.08), 2),
                0.15);
        }

        /// <summary>"Mi-a-ow": the pitch rises and falls while the vowel opens from "i" to "a" and closes to "u".</summary>
        static float[] Meow()
        {
            const double len = 0.62;
            return Voiced(len,
                t => (t < 0.19 ? 520 + 300 * Smooth(t / 0.19) : 820 - 350 * Smooth((t - 0.19) / 0.43)) * (1 + 0.012 * Math.Sin(TwoPi * 6 * t)),
                t => t < 0.22 ? 350 + 650 * Smooth(t / 0.22) : 1000 - 400 * Smooth((t - 0.22) / 0.4),
                t => 2300 - 1300 * Smooth(t / len),
                t => Math.Min(1, t / 0.06) * Math.Pow(Math.Min(1, (len - t) / 0.16), 1.5),
                0.15);
        }

        /// <summary>Rising sparkle: four bells up an F major arpeggio.</summary>
        static float[] Upgrade()
        {
            double[] f = { 1397, 1760, 2093, 2794 };
            return Gen(0.6, t =>
            {
                double v = 0;
                for (int i = 0; i < f.Length; i++) v += Ting(f[i], t - i * 0.055, 9) * (0.7 + 0.1 * i);
                return v;
            });
        }

        /// <summary>Coin down: a high clink and a lower ring.</summary>
        static float[] Sell(Random r)
        {
            return Gen(0.4, t => 0.8 * Ting(1760, t, 22) + Ting(1319, t - 0.08, 9) + (t < 0.004 ? 0.3 * Noise(r) * (1 - t / 0.004) : 0));
        }

        /// <summary>Round cleared: a short "ta-da-da-daa" on horns with a bell on top.</summary>
        static float[] Jingle()
        {
            return Gen(0.95, t =>
                Horn(523.3, t, 0.08) + Horn(698.5, t - 0.1, 0.08) + Horn(880, t - 0.2, 0.08)
                + Horn(1046.5, t - 0.3, 0.45) + 0.5 * (Horn(698.5, t - 0.3, 0.45) + Horn(880, t - 0.3, 0.45))
                + 0.3 * Ting(2093, t - 0.3, 6));
        }

        /// <summary>A little shower of coins.</summary>
        static float[] Coins()
        {
            double[] f = { 2093, 2637, 3136, 2794, 3520, 3136 }, st = { 0, 0.045, 0.09, 0.15, 0.21, 0.28 };
            return Gen(0.65, t =>
            {
                double v = 0;
                for (int i = 0; i < f.Length; i++) v += Ting(f[i], t - st[i], 13) * (1 - 0.08 * i);
                return v;
            });
        }

        /// <summary>A vacuum reached the bed: a muted "wa-waah" that droops.</summary>
        static float[] Leak()
        {
            return Voiced(0.72,
                t => t < 0.27 ? 392 : 349 - 45 * Smooth((t - 0.32) / 0.35),
                t => t < 0.27 ? 400 + 800 * Math.Sin(Math.PI * t / 0.27) : 400 + 900 * Math.Sin(Math.PI * Math.Min(1, (t - 0.27) / 0.45)),
                t => 1300,
                t => t < 0.27 ? Math.Min(1, t / 0.02) * Math.Max(0, Math.Min(1, (0.26 - t) / 0.03))
                              : t < 0.28 ? 0 : Math.Min(1, (t - 0.28) / 0.03) * Math.Min(1, (0.72 - t) / 0.12),
                0.5);
        }

        /// <summary>A boss appears: a big vacuum motor spinning up, its turbine whining and the air roaring.</summary>
        static float[] Boss(Random r)
        {
            double ph = 0, ph2 = 0, lp = 0;
            const double len = 1.8;
            return Gen(len, t =>
            {
                double u = t / len, f = 45 + 55 * u * u, motor = 0;
                ph += TwoPi * f / SR;
                for (int k = 1; k <= 24 && k * f < 4000; k++) motor += Math.Sin(k * ph) / k;
                ph2 += TwoPi * (700 + 1900 * u) / SR;
                lp += Lp(300 + 2500 * u) * (Noise(r) - lp);
                double throb = 1 + 0.3 * Math.Sin(TwoPi * (3 + 6 * u) * t);
                double e = Math.Min(1, t / 0.3) * Math.Min(1, (len - t) / 0.25);
                return (0.5 * motor * throb + 0.22 * u * Math.Sin(ph2) + 0.8 * lp) * e;
            });
        }

        /// <summary>Victory: a horn fanfare up to a held F major chord with bells.</summary>
        static float[] Win()
        {
            double[] f = { 523.3, 698.5, 880, 1046.5, 880, 1396.9 }, st = { 0, 0.1, 0.2, 0.3, 0.52, 0.62 }, du = { 0.08, 0.08, 0.08, 0.18, 0.08, 0.9 };
            double[] chord = { 349.2, 440, 523.3, 698.5 }, spark = { 1397, 1760, 2093, 2794 };
            return Gen(2.0, t =>
            {
                double v = 0;
                for (int i = 0; i < f.Length; i++) v += Horn(f[i], t - st[i], du[i]);
                foreach (double c in chord) v += 0.4 * Horn(c, t - 0.62, 0.9);
                for (int i = 0; i < spark.Length; i++) v += 0.3 * Ting(spark[i], t - 0.62 - i * 0.07, 5);
                return v;
            });
        }

        /// <summary>Game over: a muted trombone sighing down four half steps, the last note wobbling.</summary>
        static float[] Lose()
        {
            double[] f = { 233.1, 220, 207.7, 196 };
            const double step = 0.36, len = 2.0;
            return Voiced(len,
                t =>
                {
                    int i = Math.Min(3, (int)(t / step));
                    double u = t - i * step, wob = i == 3 ? 0.03 * Math.Sin(TwoPi * 5 * u) * Math.Min(1, u / 0.4) : 0;
                    return f[i] * (1 - 0.04 * Math.Exp(-u * 25) + wob) * (i == 3 ? 1 - 0.02 * u : 1);
                },
                t =>
                {
                    int i = Math.Min(3, (int)(t / step));
                    return 350 + 650 * Math.Sin(Math.PI * Math.Min(1, (t - i * step) / (i == 3 ? 0.9 : 0.32)));
                },
                t => 1200,
                t =>
                {
                    int i = Math.Min(3, (int)(t / step));
                    double u = t - i * step, d = i == 3 ? len - i * step : step - 0.03;
                    return Math.Min(1, u / 0.025) * Math.Max(0, Math.Min(1, (d - u) / (i == 3 ? 0.3 : 0.03)));
                },
                0.5);
        }

        /// <summary>UI tick.</summary>
        static float[] Click(Random r)
        {
            return Gen(0.04, t => Math.Min(1, t / 0.0005) * (Math.Sin(TwoPi * 1900 * t) * Math.Exp(-t * 160) + (t < 0.002 ? 0.3 * Noise(r) : 0)));
        }

        /// <summary>A chomp: a quick jaw thud under a crackle of crunchy grains, like biting a hard kibble.</summary>
        static float[] Bite(Random r)
        {
            double[] st = { 0, 0.017, 0.029, 0.047, 0.066 }, am = { 1, 0.7, 0.85, 0.5, 0.3 };
            double ph = 0, a = 0, b = 0;
            return Gen(0.15, t =>
            {
                ph += TwoPi * (85 + 150 * Math.Exp(-t * 45)) / SR;
                double n = Noise(r), crunch = 0;
                a += Lp(4500) * (n - a);
                b += Lp(1100) * (n - b);
                for (int i = 0; i < st.Length; i++) if (t >= st[i]) crunch += am[i] * Math.Exp(-(t - st[i]) * 170);
                return Math.Min(1, t / 0.001) * (0.8 * Math.Sin(ph) * Math.Exp(-t * 28) + 1.6 * (a - b) * crunch);
            });
        }

        /// <summary>A sniper shot: a dry crack (a snap of noise and a short knock) and a tiny whistle falling from A to F.</summary>
        static float[] Snipe(Random r)
        {
            double prev = 0, ph = 0, ph2 = 0;
            return Gen(0.24, t =>
            {
                double n = Noise(r), h = n - prev;
                prev = n;
                ph += TwoPi * (240 + 700 * Math.Exp(-t * 120)) / SR;
                ph2 += TwoPi * (2794 + 730 * Math.Exp(-t * 20)) / SR;
                double crack = 0.9 * h * Math.Exp(-t * 260) + 0.7 * Math.Sin(ph) * Math.Exp(-t * 60);
                double u = t - 0.01, whistle = u < 0 ? 0 : 0.14 * Math.Sin(ph2) * Math.Min(1, u / 0.025) * Math.Exp(-u * 14);
                return Math.Min(1, t / 0.0004) * (crack + whistle);
            });
        }

        /// <summary>A web spun across the floor: a soft elastic twang that stretches up to F and wobbles.</summary>
        static float[] Web()
        {
            double ph = 0;
            return Gen(0.38, t =>
            {
                double f = 349.2 * (1 - 0.2 * Math.Exp(-t * 16)) * (1 + 0.03 * Math.Sin(TwoPi * 9 * t) * Math.Exp(-t * 6));
                ph += TwoPi * f / SR;
                double v = Math.Sin(ph) + 0.5 * Math.Exp(-t * 14) * Math.Sin(2 * ph) + 0.3 * Math.Exp(-t * 24) * Math.Sin(3 * ph) + 0.4 * Math.Sin(0.5 * ph);
                return Math.Min(1, t / 0.004) * Math.Exp(-t * 8) * v;
            });
        }

        /// <summary>A tier 5 is bought: horns climb F-A-C, Bb-D, C-E (I-IV-V) into a held high F chord with a cascade of bells.</summary>
        static float[] Legend()
        {
            double[] f = { 698.5, 880, 1046.5, 932.3, 1174.7, 1046.5, 1318.5, 1396.9 }, st = { 0, 0.07, 0.14, 0.27, 0.34, 0.47, 0.54, 0.66 }, du = { 0.06, 0.06, 0.1, 0.06, 0.1, 0.06, 0.08, 0.45 };
            double[] bass = { 349.2, 466.2, 523.3 }, bst = { 0, 0.27, 0.47 }, chord = { 349.2, 440, 523.3, 698.5 }, spark = { 1397, 1760, 2093, 2794, 3520 };
            return Gen(1.3, t =>
            {
                double v = 0;
                for (int i = 0; i < f.Length; i++) v += Horn(f[i], t - st[i], du[i]);
                for (int i = 0; i < bass.Length; i++) v += 0.4 * Horn(bass[i], t - bst[i], 0.16);
                foreach (double c in chord) v += 0.35 * Horn(c, t - 0.66, 0.45);
                for (int i = 0; i < spark.Length; i++) v += 0.3 * Ting(spark[i], t - 0.66 - i * 0.045, 6);
                return v;
            });
        }

        // ---------- music ----------

        /// <summary>
        /// "Kočičí kavárna": an original swing tune in F major at 104 BPM, 24 bars (A A' B, 55 s), looping seamlessly.
        /// Vibraphone melody over e-piano comping, walking upright bass and brushes; a soft flute takes the bridge.
        /// </summary>
        static float[] MakeMusic()
        {
            var s = new Song(24, 104);
            int[][] voicing =
            {
                new[] { 52, 55, 57, 60 },   // 0 Fmaj9   E G A C (rootless: the bass plays the root)
                new[] { 53, 57, 60, 64 },   // 1 Dm9     F A C E
                new[] { 53, 57, 58, 62 },   // 2 Gm9     F A Bb D
                new[] { 52, 57, 58, 62 },   // 3 C13     E A Bb D
                new[] { 55, 59, 60, 64 },   // 4 Am9     G B C E
                new[] { 54, 57, 60, 63 },   // 5 D7b9    F# A C Eb
                new[] { 50, 55, 57, 60 },   // 6 F6/9    D G A C
                new[] { 51, 55, 57, 60 },   // 7 F9      Eb G A C
                new[] { 50, 53, 57, 60 },   // 8 Bbmaj9  D F A C
                new[] { 49, 53, 55, 60 },   // 9 Bbm6/9  Db F G C
                new[] { 49, 53, 55, 58 },   // 10 Eb9    Db F G Bb
                new[] { 54, 59, 60, 64 }    // 11 D13    F# B C E
            };
            // two chords per bar (first half, second half): A, A', bridge
            int[] chart =
            {
                0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 2, 3, 6, 3,
                0, 0, 7, 7, 8, 8, 9, 10, 4, 4, 5, 5, 2, 3, 6, 7,
                8, 8, 9, 10, 4, 4, 11, 11, 2, 2, 3, 3, 4, 11, 2, 3
            };
            // walking bass, a note per beat; every bar steps into the next one's root
            int[] bass =
            {
                41, 45, 48, 49, 50, 48, 45, 44, 43, 45, 46, 47, 48, 46, 43, 46, 45, 43, 40, 39, 38, 42, 45, 44, 43, 38, 36, 40, 41, 45, 43, 40,
                41, 48, 45, 40, 41, 45, 48, 45, 46, 48, 50, 47, 46, 41, 39, 43, 45, 43, 40, 39, 38, 42, 45, 44, 43, 38, 36, 40, 41, 45, 48, 51,
                46, 50, 48, 47, 46, 41, 39, 46, 45, 47, 48, 49, 50, 48, 45, 42, 43, 46, 50, 49, 48, 46, 43, 46, 45, 40, 38, 42, 43, 38, 36, 40
            };
            // bar, beat, length in beats, MIDI note. The hook: a pickup, a little chromatic paw-step (F E F) and a pounce up to A.
            double[,] tune =
            {
                { 0, 0.5, 0.5, 72 }, { 0, 1, 0.5, 77 }, { 0, 1.5, 0.5, 76 }, { 0, 2, 0.5, 77 }, { 0, 2.5, 1.5, 81 },
                { 1, 0.5, 0.5, 79 }, { 1, 1, 0.5, 77 }, { 1, 1.5, 0.5, 74 }, { 1, 2, 2, 77 },
                { 2, 0.5, 0.5, 70 }, { 2, 1, 0.5, 74 }, { 2, 1.5, 0.5, 73 }, { 2, 2, 0.5, 74 }, { 2, 2.5, 1.5, 79 },
                { 3, 0.5, 0.5, 77 }, { 3, 1, 0.5, 76 }, { 3, 1.5, 0.5, 72 }, { 3, 2, 2, 76 },
                { 4, 0.5, 0.5, 72 }, { 4, 1, 0.5, 76 }, { 4, 1.5, 0.5, 75 }, { 4, 2, 0.5, 76 }, { 4, 2.5, 1.5, 84 },
                { 5, 0.5, 0.5, 82 }, { 5, 1, 0.5, 81 }, { 5, 1.5, 0.5, 78 }, { 5, 2, 2, 81 },
                { 6, 0.5, 0.5, 79 }, { 6, 1, 0.5, 77 }, { 6, 1.5, 0.5, 74 }, { 6, 2, 0.5, 76 }, { 6, 2.5, 0.5, 79 }, { 6, 3, 0.5, 82 }, { 6, 3.5, 2.5, 81 },
                { 7, 2, 0.5, 79 }, { 7, 2.5, 0.5, 76 }, { 7, 3, 1, 74 },

                { 8, 0.5, 0.5, 72 }, { 8, 1, 0.5, 77 }, { 8, 1.5, 0.5, 76 }, { 8, 2, 0.5, 77 }, { 8, 2.5, 1.5, 81 },
                { 9, 0.5, 0.5, 79 }, { 9, 1, 0.5, 77 }, { 9, 1.5, 0.5, 74 }, { 9, 2, 2, 77 },
                { 10, 0.5, 0.5, 70 }, { 10, 1, 0.5, 74 }, { 10, 1.5, 0.5, 73 }, { 10, 2, 0.5, 74 }, { 10, 2.5, 1.5, 79 },
                { 11, 0.5, 0.5, 77 }, { 11, 1, 0.5, 73 }, { 11, 1.5, 0.5, 72 }, { 11, 2, 2, 73 },
                { 12, 0.5, 0.5, 72 }, { 12, 1, 0.5, 76 }, { 12, 1.5, 0.5, 75 }, { 12, 2, 0.5, 76 }, { 12, 2.5, 1.5, 84 },
                { 13, 0.5, 0.5, 82 }, { 13, 1, 0.5, 81 }, { 13, 1.5, 0.5, 78 }, { 13, 2, 2, 81 },
                { 14, 0.5, 0.5, 79 }, { 14, 1, 0.5, 77 }, { 14, 1.5, 0.5, 74 }, { 14, 2, 0.5, 76 }, { 14, 2.5, 0.5, 79 }, { 14, 3, 0.5, 76 }, { 14, 3.5, 3.5, 77 },
                { 15, 3, 0.5, 69 }, { 15, 3.5, 0.5, 72 },

                { 16, 0, 1.5, 74 }, { 16, 1.5, 0.5, 77 }, { 16, 2, 2, 81 },
                { 17, 0, 1.5, 79 }, { 17, 1.5, 0.5, 77 }, { 17, 2, 2, 73 },
                { 18, 0, 1.5, 72 }, { 18, 1.5, 0.5, 76 }, { 18, 2, 2, 79 },
                { 19, 0, 1.5, 78 }, { 19, 1.5, 0.5, 76 }, { 19, 2, 1, 72 }, { 19, 3, 1, 69 },
                { 20, 0, 0.5, 70 }, { 20, 0.5, 0.5, 74 }, { 20, 1, 0.5, 77 }, { 20, 1.5, 0.5, 81 }, { 20, 2, 2, 82 },
                { 21, 0, 0.5, 81 }, { 21, 0.5, 0.5, 79 }, { 21, 1, 0.5, 76 }, { 21, 1.5, 0.5, 72 }, { 21, 2, 1.5, 70 }, { 21, 3.5, 0.5, 72 },
                { 22, 0, 1, 76 }, { 22, 1, 0.5, 72 }, { 22, 1.5, 0.5, 76 }, { 22, 2, 1, 78 }, { 22, 3, 1, 81 },
                { 23, 0, 1, 82 }, { 23, 1, 0.5, 81 }, { 23, 1.5, 0.5, 79 }, { 23, 2, 0.5, 76 }, { 23, 2.5, 0.5, 79 }, { 23, 3, 1, 76 }
            };
            double[] ride = { 0, 1, 1.5, 2, 3, 3.5 };
            for (int bar = 0; bar < 24; bar++)
            {
                double b0 = bar * 4;
                int c1 = chart[bar * 2], c2 = chart[bar * 2 + 1];
                if (bar >= 16)
                {
                    // bridge: sustained pad under longer e-piano chords
                    s.Pad(b0, c1 == c2 ? 4 : 2, voicing[c1], 0.035);
                    if (c1 != c2) s.Pad(b0 + 2, 2, voicing[c2], 0.035);
                    s.EPiano(b0, c1 == c2 ? 1.8 : 1.3, voicing[c1], 0.06);
                    s.EPiano(b0 + 2.5, 1.2, voicing[c2], 0.055);
                }
                else if (c1 != c2) { s.EPiano(b0, 1.3, voicing[c1], 0.08); s.EPiano(b0 + 2, 1.3, voicing[c2], 0.075); }
                else if (bar % 2 == 0) { s.EPiano(b0, 1.3, voicing[c1], 0.08); s.EPiano(b0 + 1.5, 0.5, voicing[c1], 0.06); }
                else { s.EPiano(b0, 0.9, voicing[c1], 0.075); s.EPiano(b0 + 2.5, 1.2, voicing[c1], 0.065); }

                bool skip = bar % 4 == 3;
                for (int k = 0; k < 4; k++) s.Upright(b0 + k, skip && k == 2 ? 0.45 : 0.9, bass[bar * 4 + k], k == 0 ? 0.2 : 0.17);
                if (skip) s.Upright(b0 + 2.5, 0.4, bass[bar * 4 + 2], 0.085);   // ghosted swing skip

                s.Kick(b0, 0.14); s.Kick(b0 + 2, 0.1);
                foreach (double x in ride) s.Ride(b0 + x, x % 1 != 0 ? 0.05 : x == 1 || x == 3 ? 0.07 : 0.055);
                s.Brush(b0 + 1, 0.34); s.Brush(b0 + 3, 0.34);
                s.Chick(b0 + 1, 0.12); s.Chick(b0 + 3, 0.12);
                for (int k = 0; k < 4; k++) s.Sweep(b0 + k, 0.075);
                if (bar % 8 == 7) { s.Brush(b0 + 2.5, 0.2); s.Brush(b0 + 3.5, 0.25); s.Kick(b0 + 3.5, 0.13); }
            }
            for (int i = 0; i < tune.GetLength(0); i++)
            {
                double b = tune[i, 0] * 4 + tune[i, 1], len = tune[i, 2];
                int m = (int)tune[i, 3];
                // the flute enters on the pickup into the bridge
                if (b < 63) { s.Vibes(b, len, m, 0.21); continue; }
                double prevB = tune[i - 1, 0] * 4 + tune[i - 1, 1];
                bool legato = prevB >= 63 && Math.Abs(prevB + tune[i - 1, 2] - b) < 0.01;
                s.Flute(b, len, m, legato ? (int)tune[i - 1, 3] : 0, 0.16);
            }
            return s.Done();
        }

        /// <summary>A loop being rendered: instruments add into a dry track and a reverb send, wrapping past the end.</summary>
        sealed class Song
        {
            public readonly float[] Dry, Send;
            public readonly double Beat;
            public readonly Random Rnd = new Random(11);
            // swing of the off-beat eighths (beats), tape wobble depth (s), reverb level, top cutoff (Hz)
            const double Swing = 0.15, Wow = 0.0016, Wet = 0.4, Tone = 5200;

            public Song(int bars, double bpm)
            {
                Beat = 60.0 / bpm;
                int n = (int)(bars * 4 * Beat * SR);
                Dry = new float[n];
                Send = new float[n];
            }

            /// <summary>Swung eighths: the off-beat eighth lands late.</summary>
            static double Sw(double b) { return Math.Abs(b - Math.Floor(b) - 0.5) < 1e-6 ? b + Swing : b; }

            void Add(double b, double dur, double send, Func<double, double> f)
            {
                int start = (int)(Sw(b) * Beat * SR), n = (int)(dur * SR), len = Dry.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = f(i / (double)SR);
                    int k = (start + i) % len;
                    Dry[k] += (float)v;
                    Send[k] += (float)(v * send);
                }
            }

            static double Release(double t, double len, double rel) { return t > len ? Math.Max(0, 1 - (t - len) / rel) : 1; }

            /// <summary>Vibraphone: a sine bar with its 4th and 10th partials, the motor tremolo, damped at the end.</summary>
            public void Vibes(double b, double beats, int midi, double vol)
            {
                double f = Hz(midi), len = beats * Beat + 0.15;
                bool hi = f * 10 < 9500;
                vol *= 0.92 + 0.16 * Rnd.NextDouble();
                Add(b, len + 0.3, 0.55, t =>
                {
                    double v = Math.Sin(TwoPi * f * t) * Math.Exp(-t * 1.4) + 0.3 * Math.Sin(TwoPi * 4 * f * t) * Math.Exp(-t * 6)
                             + (hi ? 0.1 * Math.Sin(TwoPi * 10 * f * t) * Math.Exp(-t * 30) : 0);
                    return v * (1 + 0.18 * Math.Sin(TwoPi * 5.2 * t)) * vol * Math.Min(1, t / 0.0015) * Release(t, len, 0.3);
                });
            }

            /// <summary>Soft flute: a breathy attack, delayed vibrato, gliding from the previous note when legato.</summary>
            public void Flute(double b, double beats, int midi, int fromMidi, double vol)
            {
                double f0 = Hz(midi), from = fromMidi > 0 ? Hz(fromMidi) / f0 : 1, len = beats * Beat * 0.95, ph = 0, lp = 0;
                Add(b, len + 0.12, 0.45, t =>
                {
                    double vib = t > 0.2 ? 0.007 * Math.Sin(TwoPi * 5 * t) * Math.Min(1, (t - 0.2) / 0.3) : 0;
                    ph += TwoPi * f0 * Math.Pow(from, Math.Exp(-t / 0.04)) * (1 + vib) / SR;
                    lp += 0.3 * (Rnd.NextDouble() * 2 - 1 - lp);
                    double v = Math.Sin(ph) + 0.22 * Math.Sin(2 * ph) + 0.08 * Math.Sin(3 * ph) + lp * (0.08 + 0.25 * Math.Exp(-t * 18));
                    return v * vol * Math.Min(1, t / 0.045) * (0.85 + 0.15 * Math.Exp(-t * 3)) * Release(t, len, 0.12);
                });
            }

            /// <summary>FM electric piano with a gentle tremolo: a bell-like attack fading into a round tone.</summary>
            public void EPiano(double b, double beats, int[] notes, double vol)
            {
                double len = beats * Beat;
                foreach (int m in notes)
                {
                    double f = Hz(m), g = vol * (0.85 + 0.3 * Rnd.NextDouble());
                    Add(b, len + 0.3, 0.5, t =>
                    {
                        double index = 1.6 * Math.Exp(-t * 3) + 0.25;
                        double v = Math.Sin(TwoPi * f * t + index * Math.Sin(TwoPi * f * t));
                        return v * g * (1 + 0.12 * Math.Sin(TwoPi * 4.6 * t)) * Math.Min(1, t / 0.003) * Math.Exp(-t * 0.9) * Release(t, len, 0.3);
                    });
                }
            }

            /// <summary>Warm pad: two detuned saws per note through a low-pass, slow attack.</summary>
            public void Pad(double b, double beats, int[] notes, double vol)
            {
                double len = beats * Beat, k = Lp(900);
                foreach (int m in notes)
                {
                    double f = Hz(m), y = 0;
                    var a = new Saw(Rnd.NextDouble());
                    var c = new Saw(Rnd.NextDouble());
                    Add(b, len + 0.45, 0.7, t =>
                    {
                        y += k * (0.5 * (a.Next(f * 1.0035) + c.Next(f * 0.9965)) - y);
                        return y * vol * Math.Min(1, t / 0.3) * Release(t, len, 0.45);
                    });
                }
            }

            /// <summary>Upright bass: a plucked round tone, brighter at the attack.</summary>
            public void Upright(double b, double beats, int midi, double vol)
            {
                double f = Hz(midi), len = beats * Beat * 0.92, ph = 0;
                Add(b, len + 0.06, 0.08, t =>
                {
                    ph += TwoPi * f / SR;
                    double bright = Math.Exp(-t * 9);
                    double v = Math.Sin(ph) + (0.35 + 0.4 * bright) * Math.Sin(2 * ph) + 0.25 * bright * Math.Sin(3 * ph) + 0.12 * bright * Math.Sin(4 * ph);
                    return v * vol * Math.Min(1, t / 0.006) * (0.45 + 0.55 * Math.Exp(-t * 5)) * Release(t, len, 0.06);
                });
            }

            public void Kick(double b, double vol)
            {
                double ph = 0;
                Add(b, 0.3, 0, t =>
                {
                    ph += TwoPi * (46 + 80 * Math.Exp(-t * 30)) / SR;
                    return Math.Sin(ph) * Math.Exp(-t * 11) * vol;
                });
            }

            /// <summary>Ride cymbal "ting": a few inharmonic high partials and a breath of noise.</summary>
            public void Ride(double b, double vol)
            {
                double prev = 0;
                Add(b, 0.5, 0.25, t =>
                {
                    double n = Rnd.NextDouble() * 2 - 1, h = n - prev;
                    prev = n;
                    double ring = Math.Sin(TwoPi * 3170 * t) + 0.7 * Math.Sin(TwoPi * 4430 * t) + 0.5 * Math.Sin(TwoPi * 5210 * t);
                    return (ring * 0.25 + h * 0.4) * Math.Exp(-t * 7) * Math.Min(1, t / 0.002) * vol;
                });
            }

            /// <summary>Brush slap on the snare.</summary>
            public void Brush(double b, double vol)
            {
                double lp = 0, prev = 0;
                Add(b, 0.3, 0.3, t =>
                {
                    lp += 0.35 * (Rnd.NextDouble() * 2 - 1 - lp);
                    double h = lp - prev;
                    prev = lp;
                    return h * Math.Min(1, t / 0.012) * Math.Exp(-t * 10) * vol;
                });
            }

            /// <summary>The other brush circling on the snare head: a soft swish over one beat.</summary>
            public void Sweep(double b, double vol)
            {
                double lp = 0, prev = 0, len = Beat;
                Add(b, len, 0.2, t =>
                {
                    lp += 0.25 * (Rnd.NextDouble() * 2 - 1 - lp);
                    double h = lp - prev, e = Math.Sin(Math.PI * t / len);
                    prev = lp;
                    return h * e * e * vol;
                });
            }

            /// <summary>Hi-hat closed by the foot.</summary>
            public void Chick(double b, double vol)
            {
                double prev = 0, lp = 0;
                Add(b, 0.06, 0.05, t =>
                {
                    double n = Rnd.NextDouble() * 2 - 1, h = n - prev;
                    prev = n;
                    lp += 0.6 * (h - lp);
                    return lp * Math.Min(1, t / 0.002) * Math.Exp(-t * 70) * vol;
                });
            }

            /// <summary>Reverb, worn-tape wobble, a soft low-pass and gentle saturation; returns the loop at RMS 0.14.</summary>
            public float[] Done()
            {
                int n = Dry.Length;
                var wet = new float[n];
                // Schroeder reverb: 4 damped combs into 2 all-passes. Two passes over the loop so the tail wraps into the start.
                int[] cl = { 557, 593, 641, 677 }, al = { 277, 211 };
                var comb = new float[4][];
                var damp = new float[4];
                var ci = new int[4];
                for (int c = 0; c < 4; c++) comb[c] = new float[cl[c]];
                var ap = new[] { new float[al[0]], new float[al[1]] };
                var ai = new int[2];
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < n; i++)
                    {
                        float x = Send[i] * 0.25f, y = 0;
                        for (int c = 0; c < 4; c++)
                        {
                            float o = comb[c][ci[c]];
                            damp[c] = o * 0.65f + damp[c] * 0.35f;
                            comb[c][ci[c]] = x + damp[c] * 0.86f;
                            if (++ci[c] == cl[c]) ci[c] = 0;
                            y += o;
                        }
                        for (int a = 0; a < 2; a++)
                        {
                            float bo = ap[a][ai[a]], o = bo - 0.5f * y;
                            ap[a][ai[a]] = y + 0.5f * o;
                            y = o;
                            if (++ai[a] == al[a]) ai[a] = 0;
                        }
                        if (pass == 1) wet[i] = y;
                    }
                double dryRms = Rms(Dry), wetRms = Rms(wet);
                float wetGain = wetRms > 0 ? (float)(Wet * dryRms / wetRms) : 0;
                for (int i = 0; i < n; i++) Dry[i] += wet[i] * wetGain;

                // wow and flutter: the read position drifts by a millisecond or two like an old tape;
                // both rates fit a whole number of times into the loop, so it stays seamless
                double secs = n / (double)SR;
                double wow = Math.Max(1, Math.Round(0.4 * secs)) / secs, flutter = Math.Round(5.3 * secs) / secs;
                var o2 = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)SR;
                    double p = i - (Wow * (1 + Math.Sin(TwoPi * wow * t)) + 0.00007 * Math.Sin(TwoPi * flutter * t)) * SR;
                    int k = (int)Math.Floor(p);
                    double fr = p - k;
                    float a = Dry[((k % n) + n) % n], c = Dry[(((k + 1) % n) + n) % n];
                    o2[i] = (float)(a + (c - a) * fr);
                }

                // dull the top a little (two passes so the filter state wraps), then level and soft-clip
                double lk = Lp(Tone), z = 0;
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < n; i++)
                    {
                        z += lk * (o2[i] - z);
                        if (pass == 1) o2[i] = (float)z;
                    }
                double rms = Rms(o2);
                float g = rms > 0 ? (float)(0.14 / rms) : 0;
                for (int i = 0; i < n; i++) o2[i] = (float)Math.Tanh(o2[i] * g * 1.1) / 1.1f;
                return o2;
            }

            static double Rms(float[] x)
            {
                double s = 0;
                foreach (float v in x) s += v * v;
                return Math.Sqrt(s / Math.Max(1, x.Length));
            }
        }

        static double Blep(double t, double dt)
        {
            if (t < dt) { t /= dt; return t + t - t * t - 1; }
            if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
            return 0;
        }

        /// <summary>Band-limited sawtooth (polyBLEP) for the pad.</summary>
        sealed class Saw
        {
            double ph;
            public Saw(double start) { ph = start; }
            public double Next(double f)
            {
                double dt = f / SR;
                double v = 2 * ph - 1 - Blep(ph, dt);
                ph += dt;
                if (ph >= 1) ph -= 1;
                return v;
            }
        }
    }
}
