using System;
using System.Runtime.InteropServices;

namespace InvestigationNightmares.Mod.Overlay;

/// <summary>Just enough raw Direct3D 9 COM to draw textured quads. Vtable indices follow d3d9.h.</summary>
static unsafe class D3D9
{
    [DllImport("d3d9")] static extern nint Direct3DCreate9(uint sdkVersion);

    public const int D3D_OK = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct PresentParameters
    {
        public uint BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount, MultiSampleType, MultiSampleQuality, SwapEffect;
        public nint hDeviceWindow;
        public int Windowed, EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat, Flags, FullScreen_RefreshRateInHz, PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreationParameters { public uint AdapterOrdinal, DeviceType; public nint hFocusWindow; public uint BehaviorFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Viewport { public uint X, Y, Width, Height; public float MinZ, MaxZ; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LockedRect { public int Pitch; public nint Bits; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex { public float X, Y, Z, Rhw; public uint Color; public float U, V; }

    public const uint FVF = 0x004 | 0x040 | 0x100; // XYZRHW | DIFFUSE | TEX1

    static nint Fn(nint obj, int index) => (*(nint**)obj)[index];

    /// <summary>Addresses of IDirect3DDevice9::Present and ::Reset, read from a throwaway NULLREF device.</summary>
    public static (nint present, nint reset) FindDeviceFunctions()
    {
        nint d3d = Direct3DCreate9(32);
        if (d3d == 0) throw new InvalidOperationException("Direct3DCreate9 failed");
        nint wnd = Native.CreateWindowExW(0, "STATIC", "in-dummy", 0, 0, 0, 8, 8, 0, 0, 0, 0);
        try
        {
            var pp = new PresentParameters { Windowed = 1, SwapEffect = 1, BackBufferFormat = 0, hDeviceWindow = wnd, BackBufferWidth = 8, BackBufferHeight = 8 };
            nint device;
            // IDirect3D9::CreateDevice(adapter, type, focus, behavior, params, out device) — NULLREF device, software VP
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, PresentParameters*, nint*, int>)Fn(d3d, 16))(d3d, 0, 4, wnd, 0x20, &pp, &device);
            if (hr < 0) hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, PresentParameters*, nint*, int>)Fn(d3d, 16))(d3d, 0, 1, wnd, 0x20, &pp, &device);
            if (hr < 0) throw new InvalidOperationException($"dummy CreateDevice failed 0x{hr:X8}");
            var result = (Fn(device, 17), Fn(device, 16));
            Release(device);
            return result;
        }
        finally
        {
            Release(d3d);
            if (wnd != 0) Native.DestroyWindow(wnd);
        }
    }

    public static uint Release(nint obj) => obj == 0 ? 0 : ((delegate* unmanaged[Stdcall]<nint, uint>)Fn(obj, 2))(obj);

    public static nint FocusWindow(nint dev)
    {
        CreationParameters cp;
        return ((delegate* unmanaged[Stdcall]<nint, CreationParameters*, int>)Fn(dev, 9))(dev, &cp) >= 0 ? cp.hFocusWindow : 0;
    }

    public static Viewport GetViewport(nint dev)
    {
        Viewport vp;
        ((delegate* unmanaged[Stdcall]<nint, Viewport*, int>)Fn(dev, 48))(dev, &vp);
        return vp;
    }

    public static nint CreateTexture(nint dev, int w, int h, ReadOnlySpan<byte> bgra)
    {
        nint tex;
        // CreateTexture(w, h, levels=1, usage=0, D3DFMT_A8R8G8B8=21, D3DPOOL_MANAGED=1, out tex, shared=null)
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, uint, nint*, nint, int>)Fn(dev, 23))(dev, (uint)w, (uint)h, 1, 0, 21, 1, &tex, 0);
        if (hr < 0) return 0;
        LockedRect lr;
        if (((delegate* unmanaged[Stdcall]<nint, uint, LockedRect*, nint, uint, int>)Fn(tex, 19))(tex, 0, &lr, 0, 0) >= 0)
        {
            fixed (byte* src = bgra)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + y * w * 4, (byte*)lr.Bits + y * lr.Pitch, w * 4, w * 4);
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Fn(tex, 20))(tex, 0);
        }
        return tex;
    }

    public static int BeginScene(nint dev) => ((delegate* unmanaged[Stdcall]<nint, int>)Fn(dev, 41))(dev);
    public static int EndScene(nint dev) => ((delegate* unmanaged[Stdcall]<nint, int>)Fn(dev, 42))(dev);

    public static nint GetRenderTarget(nint dev)
    {
        nint s;
        return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Fn(dev, 38))(dev, 0, &s) >= 0 ? s : 0;
    }

    public static nint GetBackBuffer(nint dev)
    {
        nint s;
        return ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, nint*, int>)Fn(dev, 18))(dev, 0, 0, 0, &s) >= 0 ? s : 0;
    }

    public static void SetRenderTarget(nint dev, nint surface) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Fn(dev, 37))(dev, 0, surface);

    public static nint CreateStateBlock(nint dev)
    {
        nint sb;
        return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Fn(dev, 59))(dev, 1 /* D3DSBT_ALL */, &sb) >= 0 ? sb : 0;
    }

    public static void ApplyStateBlock(nint sb) => ((delegate* unmanaged[Stdcall]<nint, int>)Fn(sb, 5))(sb);

    public static void SetRenderState(nint dev, uint state, uint value) => ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Fn(dev, 57))(dev, state, value);
    public static void SetTexture(nint dev, uint stage, nint tex) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Fn(dev, 65))(dev, stage, tex);
    public static void SetTextureStageState(nint dev, uint stage, uint type, uint value) => ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int>)Fn(dev, 67))(dev, stage, type, value);
    public static void SetSamplerState(nint dev, uint sampler, uint type, uint value) => ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int>)Fn(dev, 69))(dev, sampler, type, value);
    public static void SetFVF(nint dev, uint fvf) => ((delegate* unmanaged[Stdcall]<nint, uint, int>)Fn(dev, 89))(dev, fvf);
    public static void SetVertexShader(nint dev, nint vs) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Fn(dev, 92))(dev, vs);
    public static void SetPixelShader(nint dev, nint ps) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Fn(dev, 107))(dev, ps);

    public static void DrawQuad(nint dev, Vertex* four) =>
        // DrawPrimitiveUP(D3DPT_TRIANGLESTRIP=5, primitiveCount=2, data, stride)
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void*, uint, int>)Fn(dev, 83))(dev, 5, 2, four, (uint)sizeof(Vertex));

    /// <summary>Render states for 2D alpha-blended quads drawn over the game's frame.</summary>
    public static void Setup2D(nint dev)
    {
        SetVertexShader(dev, 0);
        SetPixelShader(dev, 0);
        SetFVF(dev, FVF);
        SetRenderState(dev, 7, 0);    // ZENABLE
        SetRenderState(dev, 14, 0);   // ZWRITEENABLE
        SetRenderState(dev, 15, 0);   // ALPHATESTENABLE
        SetRenderState(dev, 8, 3);    // FILLMODE solid
        SetRenderState(dev, 22, 1);   // CULLMODE none
        SetRenderState(dev, 27, 1);   // ALPHABLENDENABLE
        SetRenderState(dev, 171, 1);  // BLENDOP add
        SetRenderState(dev, 19, 5);   // SRCBLEND srcalpha
        SetRenderState(dev, 20, 6);   // DESTBLEND invsrcalpha
        SetRenderState(dev, 206, 0);  // SEPARATEALPHABLENDENABLE
        SetRenderState(dev, 28, 0);   // FOGENABLE
        SetRenderState(dev, 52, 0);   // STENCILENABLE
        SetRenderState(dev, 137, 0);  // LIGHTING
        SetRenderState(dev, 174, 0);  // SCISSORTESTENABLE
        SetRenderState(dev, 168, 0xF);// COLORWRITEENABLE
        SetRenderState(dev, 194, 0);  // SRGBWRITEENABLE
        SetTextureStageState(dev, 0, 1, 4); // COLOROP modulate
        SetTextureStageState(dev, 0, 2, 2); // COLORARG1 texture
        SetTextureStageState(dev, 0, 3, 0); // COLORARG2 diffuse
        SetTextureStageState(dev, 0, 4, 4); // ALPHAOP modulate
        SetTextureStageState(dev, 0, 5, 2);
        SetTextureStageState(dev, 0, 6, 0);
        SetTextureStageState(dev, 1, 1, 1); // stage 1 COLOROP disable
        SetTextureStageState(dev, 1, 4, 1);
        SetSamplerState(dev, 0, 1, 3); // ADDRESSU clamp
        SetSamplerState(dev, 0, 2, 3); // ADDRESSV clamp
        SetSamplerState(dev, 0, 5, 2); // MAGFILTER linear
        SetSamplerState(dev, 0, 6, 2); // MINFILTER linear
        SetSamplerState(dev, 0, 7, 0); // MIPFILTER none
    }
}
