using System.Runtime.InteropServices;

namespace Bridger.Core;

// Bionic/glibc üzerinde gömülü JVM başlatmak için en düşük seviyeli JNI köprüsü.
// Yalnızca gömülü JVM (Android) kullanır; masaüstünde derlenir ama çağrılmaz.
// Not: JNIEnv yalnızca onu oluşturan (veya AttachCurrentThread edilmiş) iş
// parçacığında geçerlidir. Tablo indeksleri OpenJDK jni.h ile sabittir (ABI).
internal static class Jni
{
    internal const int RTLD_NOW = 0x00002;
    internal const int RTLD_GLOBAL = 0x00100;
    internal const int JNI_VERSION_1_6 = 0x00010006;

    // --- JNINativeInterface_ indeksleri (OpenJDK jni.h; sürümden bağımsız) ---
    internal const int iFindClass = 6;
    internal const int iExceptionOccurred = 15;
    internal const int iExceptionDescribe = 16;
    internal const int iExceptionClear = 17;
    internal const int iNewGlobalRef = 21;
    internal const int iDeleteLocalRef = 23;
    internal const int iNewObjectA = 30;
    internal const int iGetMethodID = 33;
    internal const int iCallVoidMethod = 61;
    internal const int iGetStaticMethodID = 113;
    internal const int iCallStaticVoidMethodA = 143;
    internal const int iNewStringUTF = 167;
    internal const int iNewObjectArray = 172;
    internal const int iSetObjectArrayElement = 174;

    // --- JNIInvokeInterface_ (JavaVM*) ---
    internal const int vAttachCurrentThread = 4;

    [DllImport("libdl.so", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen(string dosya, int bayrak);

    // Kütüphaneyi global sembol görünürlüğüyle yükle: sonra yüklenen .so
    // dosyaları öncekilerin sembollerini bulabilsin (Pojav zinciri).
    internal static IntPtr Dlopen(string dosya) => dlopen(dosya, RTLD_NOW | RTLD_GLOBAL);

    // env/vm işaretçisinden işlev tablosu girişi (ikisi de aynı biçim: *tablo).
    private static IntPtr Islev(IntPtr kok, int indeks) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(kok), indeks * IntPtr.Size);

    private static T Fn<T>(IntPtr kok, int indeks) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Islev(kok, indeks));

    // ---------- delege imzaları ----------
    private delegate IntPtr D_FindClass(IntPtr env, [MarshalAs(UnmanagedType.LPStr)] string ad);
    private delegate IntPtr D_ExceptionOccurred(IntPtr env);
    private delegate void D_ExceptionDescribe(IntPtr env);
    private delegate void D_ExceptionClear(IntPtr env);
    private delegate IntPtr D_NewGlobalRef(IntPtr env, IntPtr nesne);
    private delegate void D_DeleteLocalRef(IntPtr env, IntPtr nesne);
    private delegate IntPtr D_NewObjectA(IntPtr env, IntPtr sinif, IntPtr mid, IntPtr args);
    private delegate IntPtr D_GetMethodID(IntPtr env, IntPtr sinif,
        [MarshalAs(UnmanagedType.LPStr)] string ad, [MarshalAs(UnmanagedType.LPStr)] string imza);
    private delegate void D_CallVoidMethodI(IntPtr env, IntPtr nesne, IntPtr mid, int a1);
    private delegate void D_CallVoidMethodRef(IntPtr env, IntPtr nesne, IntPtr mid, IntPtr a1);
    private delegate IntPtr D_GetStaticMethodID(IntPtr env, IntPtr sinif,
        [MarshalAs(UnmanagedType.LPStr)] string ad, [MarshalAs(UnmanagedType.LPStr)] string imza);
    private delegate void D_CallStaticVoidMethodA(IntPtr env, IntPtr sinif, IntPtr mid, IntPtr args);
    private delegate IntPtr D_NewStringUTF(IntPtr env, [MarshalAs(UnmanagedType.LPStr)] string s);
    private delegate IntPtr D_NewObjectArray(IntPtr env, int uzunluk, IntPtr sinif, IntPtr ilk);
    private delegate void D_SetObjectArrayElement(IntPtr env, IntPtr dizi, int i, IntPtr deger);
    private delegate int D_CreateJavaVM(IntPtr pvm, IntPtr penv, IntPtr args);
    private delegate int D_AttachThread(IntPtr vm, IntPtr penv, IntPtr args);

    // ---------- Java istisna denetimi ----------
    private static void HataVar(IntPtr env, string islem)
    {
        IntPtr ex = Fn<D_ExceptionOccurred>(env, iExceptionOccurred)(env);
        if (ex == IntPtr.Zero) return;
        try { Fn<D_ExceptionDescribe>(env, iExceptionDescribe)(env); } catch { }
        Fn<D_ExceptionClear>(env, iExceptionClear)(env);
        throw new InvalidOperationException("JNI: " + islem + " başarısız (Java istisnası).");
    }

    // ---------- sarmalayıcılar ----------
    internal static IntPtr FindClass(IntPtr env, string ad)
    {
        IntPtr r = Fn<D_FindClass>(env, iFindClass)(env, ad);
        HataVar(env, "FindClass(" + ad + ")");
        return r;
    }

    internal static IntPtr GetMethodID(IntPtr env, IntPtr sinif, string ad, string imza)
    {
        IntPtr r = Fn<D_GetMethodID>(env, iGetMethodID)(env, sinif, ad, imza);
        HataVar(env, "GetMethodID(" + ad + ")");
        return r;
    }

    internal static IntPtr GetStaticMethodID(IntPtr env, IntPtr sinif, string ad, string imza)
    {
        IntPtr r = Fn<D_GetStaticMethodID>(env, iGetStaticMethodID)(env, sinif, ad, imza);
        HataVar(env, "GetStaticMethodID(" + ad + ")");
        return r;
    }

    internal static IntPtr NewStringUTF(IntPtr env, string s)
    {
        IntPtr r = Fn<D_NewStringUTF>(env, iNewStringUTF)(env, s);
        HataVar(env, "NewStringUTF");
        return r;
    }

    internal static IntPtr NewObjectArray(IntPtr env, int uzunluk, IntPtr sinif, IntPtr ilk)
    {
        IntPtr r = Fn<D_NewObjectArray>(env, iNewObjectArray)(env, uzunluk, sinif, ilk);
        HataVar(env, "NewObjectArray");
        return r;
    }

    internal static void SetObjectArrayElement(IntPtr env, IntPtr dizi, int i, IntPtr deger)
    {
        Fn<D_SetObjectArrayElement>(env, iSetObjectArrayElement)(env, dizi, i, deger);
        HataVar(env, "SetObjectArrayElement");
    }

    internal static IntPtr NewGlobalRef(IntPtr env, IntPtr nesne)
    {
        IntPtr r = Fn<D_NewGlobalRef>(env, iNewGlobalRef)(env, nesne);
        HataVar(env, "NewGlobalRef");
        return r;
    }

    internal static void DeleteLocalRef(IntPtr env, IntPtr nesne)
    {
        try { Fn<D_DeleteLocalRef>(env, iDeleteLocalRef)(env, nesne); } catch { }
    }

    // jvalue[] bloğu: her giriş 8 bayt (birlik) — nesne işaretçisi ya da int.
    internal static IntPtr JValueBlogu(params IntPtr[] degerler)
    {
        IntPtr blok = Marshal.AllocHGlobal(Math.Max(degerler.Length, 1) * IntPtr.Size);
        for (int i = 0; i < degerler.Length; i++)
            Marshal.WriteIntPtr(blok, i * IntPtr.Size, degerler[i]);
        return blok;
    }

    internal static IntPtr NewObject(IntPtr env, IntPtr sinif, IntPtr kurucuMid, params IntPtr[] args)
    {
        IntPtr blok = JValueBlogu(args);
        try
        {
            IntPtr r = Fn<D_NewObjectA>(env, iNewObjectA)(env, sinif, kurucuMid, blok);
            HataVar(env, "NewObject");
            return r;
        }
        finally { Marshal.FreeHGlobal(blok); }
    }

    // void write(int) — PipedOutputStream'a tek karakter yazmak için.
    internal static void CallVoidMethodInt(IntPtr env, IntPtr nesne, IntPtr mid, int deger)
    {
        Fn<D_CallVoidMethodI>(env, iCallVoidMethod)(env, nesne, mid, deger);
        HataVar(env, "CallVoidMethod");
    }

    // void connect(PipedInputStream) — nesne argümanlı void çağrı.
    internal static void CallVoidMethodRef(IntPtr env, IntPtr nesne, IntPtr mid, IntPtr deger)
    {
        Fn<D_CallVoidMethodRef>(env, iCallVoidMethod)(env, nesne, mid, deger);
        HataVar(env, "CallVoidMethod");
    }

    internal static void CallStaticVoidMethodA(IntPtr env, IntPtr sinif, IntPtr mid, params IntPtr[] args)
    {
        IntPtr blok = JValueBlogu(args);
        try
        {
            Fn<D_CallStaticVoidMethodA>(env, iCallStaticVoidMethodA)(env, sinif, mid, blok);
            HataVar(env, "CallStaticVoidMethod");
        }
        finally { Marshal.FreeHGlobal(blok); }
    }

    // ---------- VM ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct JavaVMInitArgs
    {
        public int version;
        public int nOptions;
        public IntPtr options;
        public byte ignoreUnrecognized;
    }

    internal static (IntPtr vm, IntPtr env) CreateJavaVm(IntPtr createFn, string[] secenekler)
    {
        int n = secenekler.Length;
        IntPtr opts = Marshal.AllocHGlobal(16 * Math.Max(n, 1));
        var metinler = new IntPtr[n];
        IntPtr argsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<JavaVMInitArgs>());
        IntPtr vmOut = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr envOut = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            for (int i = 0; i < n; i++)
            {
                metinler[i] = Marshal.StringToHGlobalAnsi(secenekler[i]);
                Marshal.WriteIntPtr(opts, i * 16, metinler[i]);
                Marshal.WriteIntPtr(opts, i * 16 + 8, IntPtr.Zero);
            }
            var args = new JavaVMInitArgs
            {
                version = JNI_VERSION_1_6,
                nOptions = n,
                options = opts,
                ignoreUnrecognized = 0,
            };
            Marshal.StructureToPtr(args, argsPtr, false);
            var fn = Marshal.GetDelegateForFunctionPointer<D_CreateJavaVM>(createFn);
            int rc = fn(vmOut, envOut, argsPtr);
            if (rc != 0)
                throw new InvalidOperationException($"JNI_CreateJavaVM başarısız (kod {rc}).");
            return (Marshal.ReadIntPtr(vmOut), Marshal.ReadIntPtr(envOut));
        }
        finally
        {
            foreach (IntPtr p in metinler) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(opts);
            Marshal.FreeHGlobal(argsPtr);
            Marshal.FreeHGlobal(vmOut);
            Marshal.FreeHGlobal(envOut);
        }
    }

    internal static IntPtr AttachThread(IntPtr vm)
    {
        IntPtr envOut = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            var fn = Fn<D_AttachThread>(vm, vAttachCurrentThread);
            int rc = fn(vm, envOut, IntPtr.Zero);
            if (rc != 0) throw new InvalidOperationException($"AttachCurrentThread başarısız (kod {rc}).");
            return Marshal.ReadIntPtr(envOut);
        }
        finally { Marshal.FreeHGlobal(envOut); }
    }

    // Süreç çalışma dizinini değiştirir. Paperclip gibi göreli yollar ("cache/...")
    // kullanan Java kodları için şart: Android'de süreç CWD'si "/" olur ve göreli
    // yollar yazılamayan sistem dizinlerine çözülür (ör. /cache).
    [DllImport("libc", EntryPoint = "chdir", CharSet = CharSet.Ansi)]
    private static extern int libc_chdir(string yol);

    public static int Chdir(string yol) => libc_chdir(yol);
}
