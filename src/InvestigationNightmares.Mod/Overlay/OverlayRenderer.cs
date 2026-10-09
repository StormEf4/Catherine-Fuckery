using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using InvestigationNightmares.Content;
using InvestigationNightmares.Generated;
using InvestigationNightmares.Images;
using InvestigationNightmares.Story;

namespace InvestigationNightmares.Mod.Overlay;

/// <summary>What is on screen right now. Written by <see cref="Presentation"/> and read by the renderer, both on the render thread.</summary>
sealed class OverlayState
{
    public (DialogueRow line, CharactersRow speaker, bool modal)? Line;
    public (List<CharactersRow> members, int selected)? Menu;
    public (bool fadeIn, double start, double seconds)? Static;
    public (PowersRow power, CharactersRow owner, double start)? CutIn;
    public CharactersRow? BossPlate;
    public readonly List<(string text, double until)> Toasts = new();
    public List<(PowersRow power, int charges)> Hud = new();
}

/// <summary>Draws <see cref="OverlayState"/> with textured quads, Persona 4-style: black boxes, yellow rules, character colors.</summary>
sealed unsafe class OverlayRenderer
{
    sealed class Tex { public nint Handle; public int W, H; public double LastUsed; }

    readonly Func<P4GContent?> _content;
    readonly Dictionary<string, Tex> _textures = new();
    nint _white;
    nint _noise;
    double _noiseAt = -1;
    readonly Random _rng = new(4);
    nint _device;

    static readonly Color Yellow = Color.FromArgb(255, 255, 225, 0);
    const uint Black = 0xE0000000, YellowArgb = 0xFFFFE100;

    public OverlayRenderer(Func<P4GContent?> content) => _content = content;

    /// <summary>Managed-pool textures survive Reset; they only go when the device itself changes.</summary>
    public void OnDevice(nint device)
    {
        if (device == _device) return;
        ReleaseAll();
        _device = device;
    }

    public void ReleaseAll()
    {
        foreach (var t in _textures.Values) D3D9.Release(t.Handle);
        _textures.Clear();
        D3D9.Release(_white); _white = 0;
        D3D9.Release(_noise); _noise = 0;
    }

    public void Draw(nint dev, OverlayState s, double now)
    {
        var vp = D3D9.GetViewport(dev);
        float W = vp.Width, H = vp.Height;
        if (W < 64 || H < 64) return;
        if (_white == 0) _white = D3D9.CreateTexture(dev, 1, 1, new byte[] { 255, 255, 255, 255 });

        if (s.Static is { } st) DrawStatic(dev, W, H, st, now);
        DrawHud(dev, s, W, H, now);
        if (s.BossPlate is { } boss) DrawBossPlate(dev, boss, W, H, now);
        if (s.CutIn is { } cut) DrawCutIn(dev, cut, W, H, now);
        if (s.Menu is { } menu) DrawMenu(dev, menu.members, menu.selected, W, H, now);
        if (s.Line is { } line) { if (line.modal) DrawModalLine(dev, line.line, line.speaker, W, H, now); else DrawClimbLine(dev, line.line, line.speaker, W, H, now); }
        DrawToasts(dev, s, W, H, now);
        Evict(now);
    }

    // ------------------------------------------------------------------ pieces

    void DrawStatic(nint dev, float W, float H, (bool fadeIn, double start, double seconds) st, double now)
    {
        double t = st.seconds <= 0 ? 1 : Math.Clamp((now - st.start) / st.seconds, 0, 1);
        double a = st.fadeIn ? t : 1 - t;
        if (a <= 0.01) return;
        if (now - _noiseAt > 0.05 || _noise == 0)
        {
            _noiseAt = now;
            D3D9.Release(_noise);
            const int N = 160;
            var px = new byte[N * N * 4];
            for (int i = 0; i < N * N; i++)
            {
                // TV snow with the Midnight Channel's scanlines.
                int v = _rng.Next(40, 230);
                if ((i / N) % 3 == 0) v /= 2;
                px[i * 4] = (byte)Math.Min(255, v + 10); px[i * 4 + 1] = (byte)v; px[i * 4 + 2] = (byte)(v * 0.9); px[i * 4 + 3] = 255;
            }
            _noise = D3D9.CreateTexture(dev, N, N, px);
        }
        Quad(dev, _noise, 0, 0, W, H, Argb((byte)(a * 235), 255, 255, 255));
    }

    void DrawHud(nint dev, OverlayState s, float W, float H, double now)
    {
        if (s.Hud.Count == 0) return;
        float x = W * 0.015f, y = H * 0.02f;
        foreach (var (p, charges) in s.Hud)
        {
            var key = SheetIndex.Bindings[p.Hotkey].Vk.Replace("VK_", "");
            var owner = SheetIndex.Characters[p.Owner];
            var tex = Text(dev, $"[{key}] {owner.Persona} · {p.Skill}  ×{charges}", H * 0.026f, charges > 0 ? Color.White : Color.Gray, (int)(W * 0.4f), true, now, Color.Black);
            Quad(dev, _white, x - 6, y - 2, tex.W + 12, tex.H + 4, Black);
            Quad(dev, _white, x - 6, y - 2, 4, tex.H + 4, ArgbOf(TextPainter.ParseHex(owner.NameplateColor)));
            Quad(dev, tex.Handle, x, y, tex.W, tex.H, 0xFFFFFFFF);
            y += tex.H + 6;
        }
    }

    void DrawBossPlate(nint dev, CharactersRow boss, float W, float H, double now)
    {
        var tex = Text(dev, boss.Name.ToUpperInvariant(), H * 0.05f, Yellow, (int)W, true, now, Color.Black);
        float x = (W - tex.W) / 2, y = H * 0.025f;
        Quad(dev, _white, 0, y - 6, W, tex.H + 12, 0xC0000000);
        Quad(dev, _white, 0, y + tex.H + 4, W, 3, YellowArgb);
        Quad(dev, tex.Handle, x, y, tex.W, tex.H, 0xFFFFFFFF);
    }

    void DrawCutIn(nint dev, (PowersRow power, CharactersRow owner, double start) c, float W, float H, double now)
    {
        double dur = SheetIndex.Tuning("cutin_seconds");
        double t = (now - c.start) / dur;
        if (t < 0 || t > 1) return;
        float slide = (float)(t < 0.2 ? t / 0.2 : t > 0.8 ? (1 - t) / 0.2 : 1);
        uint band = ArgbOf(TextPainter.ParseHex(c.owner.NameplateColor), (byte)(220 * slide));
        float y = H * 0.38f, h = H * 0.18f;
        Quad(dev, _white, 0, y, W, h, band);
        Quad(dev, _white, 0, y, W, 4, YellowArgb);
        Quad(dev, _white, 0, y + h - 4, W, 4, YellowArgb);
        var card = CutInImage(dev, c.owner, now);
        if (card != null)
        {
            float ch = h * 1.5f, cw = card.W * ch / card.H;
            Quad(dev, card.Handle, W * 0.03f + (1 - slide) * -W * 0.3f, y + (h - ch) / 2, cw, ch, Argb((byte)(255 * slide), 255, 255, 255));
        }
        var portrait = Portrait(dev, c.owner, 1, now);
        if (portrait != null)
        {
            float ph = h * 1.6f, pw = portrait.W * ph / portrait.H;
            float px = W - pw * slide - W * 0.02f;
            Quad(dev, portrait.Handle, px, y + h - ph, pw, ph, Argb((byte)(255 * slide), 255, 255, 255));
        }
        var tex = Text(dev, $"{c.owner.Persona.ToUpperInvariant()}  —  {c.power.Skill.ToUpperInvariant()}", h * 0.42f, Color.White, (int)(W * 0.7f), true, now, Color.Black);
        Quad(dev, tex.Handle, W * (card != null ? 0.22f : 0.06f) - (1 - slide) * W * 0.3f, y + (h - tex.H) / 2, tex.W, tex.H, Argb((byte)(255 * slide), 255, 255, 255));
    }

    void DrawMenu(nint dev, List<CharactersRow> members, int selected, float W, float H, double now)
    {
        float x = W * 0.06f, y = H * 0.18f, w = W * 0.36f, rowH = H * 0.075f;
        var title = Text(dev, "MIDNIGHT CHANNEL — INVESTIGATION TEAM", H * 0.03f, Yellow, (int)w, true, now);
        Quad(dev, _white, x - 12, y - title.H - 18, w + 24, title.H + 12 + rowH * members.Count + 24, 0xD8000000);
        Quad(dev, _white, x - 12, y - 8, w + 24, 3, YellowArgb);
        Quad(dev, title.Handle, x, y - title.H - 12, title.W, title.H, 0xFFFFFFFF);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            float ry = y + i * rowH;
            if (i == selected) Quad(dev, _white, x - 8, ry, w + 16, rowH - 6, ArgbOf(TextPainter.ParseHex(m.NameplateColor), 200));
            var name = Text(dev, $"{m.Name}   ({m.Persona})", rowH * 0.42f, i == selected ? Color.White : Color.LightGray, (int)w, i == selected, now);
            Quad(dev, name.Handle, x, ry + (rowH - 6 - name.H) / 2, name.W, name.H, 0xFFFFFFFF);
        }
        var talk = SheetIndex.Bindings["mod_advance"]; var cancel = SheetIndex.Bindings["mod_cancel"];
        var hint = Text(dev, $"{talk.Vk.Replace("VK_", "")} / {talk.XinputButtons}: talk    {cancel.Vk.Replace("VK_", "")} / {cancel.XinputButtons}: leave", H * 0.022f, Color.Gray, (int)w, false, now);
        Quad(dev, hint.Handle, x, y + rowH * members.Count + 2, hint.W, hint.H, 0xFFFFFFFF);

        var sel = members[Math.Clamp(selected, 0, members.Count - 1)];
        var p = Portrait(dev, sel, 0, now);
        if (p != null)
        {
            float ph = H * (float)SheetIndex.Tuning("portrait_height"), pw = p.W * ph / p.H;
            Quad(dev, p.Handle, W * 0.62f, H - ph, pw, ph, 0xFFFFFFFF);
        }
    }

    void DrawModalLine(nint dev, DialogueRow line, CharactersRow speaker, float W, float H, double now)
    {
        var p = Portrait(dev, speaker, line.Expression, now);
        if (p != null)
        {
            float ph = H * (float)SheetIndex.Tuning("portrait_height"), pw = p.W * ph / p.H;
            Quad(dev, p.Handle, W * 0.06f, H * 0.74f - ph * 0.92f, pw, ph, 0xFFFFFFFF);
        }
        float bx = W * 0.05f, by = H * 0.72f, bw = W * 0.9f, bh = H * 0.24f;
        Quad(dev, _white, bx, by, bw, bh, 0xE6000000);
        Quad(dev, _white, bx, by, bw, 3, YellowArgb);
        Quad(dev, _white, bx, by + bh - 3, bw, 3, YellowArgb);
        var name = Text(dev, speaker.Name, H * 0.034f, Color.Black, (int)(W * 0.4f), true, now);
        Quad(dev, _white, bx + W * 0.02f, by - name.H - 4, name.W + 24, name.H + 6, ArgbOf(TextPainter.ParseHex(speaker.NameplateColor)));
        Quad(dev, name.Handle, bx + W * 0.02f + 12, by - name.H - 1, name.W, name.H, 0xFFFFFFFF);
        var body = Text(dev, line.Text, H * 0.036f, Color.White, (int)(bw - W * 0.06f), false, now);
        Quad(dev, body.Handle, bx + W * 0.03f, by + H * 0.03f, body.W, body.H, 0xFFFFFFFF);
        if ((int)(now * 2) % 2 == 0)
        {
            var next = Text(dev, "▼", H * 0.03f, Yellow, 64, true, now);
            Quad(dev, next.Handle, bx + bw - next.W - 16, by + bh - next.H - 10, next.W, next.H, 0xFFFFFFFF);
        }
    }

    void DrawClimbLine(nint dev, DialogueRow line, CharactersRow speaker, float W, float H, double now)
    {
        float bw = W * 0.4f, bx = W - bw - W * 0.02f, by = H * 0.13f;
        var body = Text(dev, line.Text, H * 0.027f, Color.White, (int)(bw * 0.68f), false, now);
        float bh = Math.Max(body.H + 20, H * 0.12f);
        Quad(dev, _white, bx, by, bw, bh, 0xD0000000);
        Quad(dev, _white, bx, by, 4, bh, ArgbOf(TextPainter.ParseHex(speaker.NameplateColor)));
        var p = Portrait(dev, speaker, line.Expression, now);
        float textX = bx + 14;
        if (p != null)
        {
            float ph = bh - 8, pw = Math.Min(bw * 0.28f, p.W * ph / p.H);
            Quad(dev, p.Handle, bx + 8, by + 4, pw, ph, 0xFFFFFFFF);
            textX = bx + 16 + pw;
        }
        var name = Text(dev, speaker.Name, H * 0.024f, Yellow, (int)(bw * 0.6f), true, now);
        Quad(dev, name.Handle, textX, by + 4, name.W, name.H, 0xFFFFFFFF);
        Quad(dev, body.Handle, textX, by + 6 + name.H, body.W, body.H, 0xFFFFFFFF);
    }

    void DrawToasts(nint dev, OverlayState s, float W, float H, double now)
    {
        s.Toasts.RemoveAll(t => t.until < now);
        float y = H * 0.66f;
        foreach (var (text, until) in s.Toasts.AsEnumerable().Reverse())
        {
            var tex = Text(dev, text, H * 0.024f, Color.White, (int)(W * 0.45f), false, now);
            byte a = (byte)(Math.Clamp(until - now, 0, 0.5) / 0.5 * 255);
            y -= tex.H + 14;
            Quad(dev, _white, W * 0.02f, y, tex.W + 20, tex.H + 10, Argb((byte)(a * 0.85), 0, 0, 0));
            Quad(dev, _white, W * 0.02f, y, 3, tex.H + 10, Argb(a, 255, 225, 0));
            Quad(dev, tex.Handle, W * 0.02f + 10, y + 5, tex.W, tex.H, Argb(a, 255, 255, 255));
        }
    }

    // ------------------------------------------------------------------ textures

    Tex Text(nint dev, string text, float size, Color color, int maxWidth, bool bold, double now, Color? outline = null)
    {
        string key = $"t|{text}|{size:F0}|{color.ToArgb()}|{maxWidth}|{bold}|{outline?.ToArgb()}";
        if (_textures.TryGetValue(key, out var t)) { t.LastUsed = now; return t; }
        var img = TextPainter.Render(text, size, color, maxWidth, bold, outline);
        t = new Tex { Handle = D3D9.CreateTexture(dev, img.Width, img.Height, img.Pixels), W = img.Width, H = img.Height, LastUsed = now };
        _textures[key] = t;
        return t;
    }

    Tex? Portrait(nint dev, CharactersRow ch, int expression, double now)
    {
        string key = $"p|{ch.Id}|{expression}";
        if (_textures.TryGetValue(key, out var t)) { t.LastUsed = now; return t.Handle == 0 ? null : t; }
        var content = _content();
        if (content == null) return null; // P4G still loading: ask again next frame
        var img = content.Portrait(ch, expression);
        t = new Tex { LastUsed = double.MaxValue }; // never evicted; Handle 0 = no portrait
        if (img != null)
        {
            var (x, y, w, h) = img.OpaqueBounds();
            var crop = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(img.Pixels, ((y + row) * img.Width + x) * 4, crop, row * w * 4, w * 4);
            t.Handle = D3D9.CreateTexture(dev, w, h, crop);
            t.W = w; t.H = h;
        }
        _textures[key] = t;
        return t.Handle == 0 ? null : t;
    }

    Tex? CutInImage(nint dev, CharactersRow ch, double now)
    {
        string key = $"c|{ch.Id}";
        if (_textures.TryGetValue(key, out var t)) { t.LastUsed = now; return t.Handle == 0 ? null : t; }
        var content = _content();
        if (content == null) return null;
        var img = content.CutIn(ch);
        t = new Tex { LastUsed = double.MaxValue };
        if (img != null) { t.Handle = D3D9.CreateTexture(dev, img.Width, img.Height, img.Pixels); t.W = img.Width; t.H = img.Height; }
        _textures[key] = t;
        return t.Handle == 0 ? null : t;
    }

    void Evict(double now)
    {
        if (_textures.Count < 96) return;
        foreach (var kv in _textures.Where(kv => kv.Value.LastUsed < now - 5).ToList())
        {
            D3D9.Release(kv.Value.Handle);
            _textures.Remove(kv.Key);
        }
    }

    // ------------------------------------------------------------------ primitives

    static uint Argb(byte a, byte r, byte g, byte b) => (uint)(a << 24 | r << 16 | g << 8 | b);
    static uint ArgbOf(Color c, byte a = 255) => Argb(a, c.R, c.G, c.B);

    static void Quad(nint dev, nint tex, float x, float y, float w, float h, uint color)
    {
        if (tex == 0) return;
        D3D9.SetTexture(dev, 0, tex);
        var v = stackalloc D3D9.Vertex[4];
        x -= 0.5f; y -= 0.5f; // D3D9 texel-to-pixel alignment
        v[0] = new D3D9.Vertex { X = x, Y = y, Z = 0, Rhw = 1, Color = color, U = 0, V = 0 };
        v[1] = new D3D9.Vertex { X = x + w, Y = y, Z = 0, Rhw = 1, Color = color, U = 1, V = 0 };
        v[2] = new D3D9.Vertex { X = x, Y = y + h, Z = 0, Rhw = 1, Color = color, U = 0, V = 1 };
        v[3] = new D3D9.Vertex { X = x + w, Y = y + h, Z = 0, Rhw = 1, Color = color, U = 1, V = 1 };
        D3D9.DrawQuad(dev, v);
    }
}
