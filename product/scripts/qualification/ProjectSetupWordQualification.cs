using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AeroLink.Api.Tests;

internal static class WordQualification
{
    [STAThread]
    static int Main()
    {
        if (Process.GetProcessesByName("WINWORD").Length != 0) throw new InvalidOperationException("Close existing Word sessions before isolated qualification.");
        var root = Path.Combine(Path.GetTempPath(), "aerolink-1037-word-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Owned Word evidence: " + root);
        try
        {
            var qualify = typeof(ManagedDocumentApiTests).GetMethod("QualifyFreshProjectManagedDocumentAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            ((Task)qualify.Invoke(null, new object[] { (Func<byte[],byte[]>)(bytes => OnSta(() => Author(bytes, root))), (Func<byte[],(byte[] Docx,byte[] Pdf)>)(bytes => OnSta(() => Render(bytes, root))) })!).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = "Passed", scope = "Fresh project, actual Word save/check-in, technical review, production Word release renderer, exact DOCX/PDF acceptance and signature", files = Directory.GetFiles(root).Where(p => p.EndsWith(".docx") || p.EndsWith(".pdf")).Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() }) }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: actual Word authoring and release renderer with Fresh-project controlled API lifecycle.");
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(root,"failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    static T OnSta<T>(Func<T> action)
    {
        T? result=default; Exception? failure=null;
        var thread=new Thread(() => { try { result=action(); } catch(Exception ex) { failure=ex; } }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Owned Word automation did not complete; retain diagnostics and inspect its process.");
        if(failure is not null) throw failure; return result!;
    }
    static byte[] Author(byte[] bytes,string root)
    {
        var source=Path.Combine(root,"fresh-plan-source.docx");var edited=Path.Combine(root,"fresh-plan-word-authored.docx");File.WriteAllBytes(source,bytes);
        dynamic? word=null,document=null;
        try
        {
            word=Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!);word!.Visible=false;word.DisplayAlerts=0;
            File.WriteAllText(Path.Combine(root,"word-version.txt"),(string)word.Version+" build "+(string)word.Build);
            document=word.Documents.Open(source,ReadOnly:false,AddToRecentFiles:false);
            dynamic range=document.Content;range.InsertAfter("\rIsolated new-project Word qualification. The plan retains the independently configured project scope.\r");Marshal.FinalReleaseComObject(range);
            document.SaveAs2(edited,16,AddToRecentFiles:false);
        }
        finally { if(document is not null){try{document.Close(false);}finally{Marshal.FinalReleaseComObject(document);}}if(word is not null){try{word.Quit();}finally{Marshal.FinalReleaseComObject(word);}} }
        return File.ReadAllBytes(edited);
    }
    static (byte[] Docx,byte[] Pdf) Render(byte[] bytes,string root)
    {
        var source=Path.Combine(root,"fresh-plan-release-source.docx");var docx=Path.Combine(root,"fresh-plan-RELEASE.docx");var pdf=Path.Combine(root,"fresh-plan-RELEASE.pdf");File.WriteAllBytes(source,bytes);
        var renderer=Assembly.Load("AeroLink.DocumentConnector").GetType("AeroLink.DocumentConnector.WordReleaseRenderer",throwOnError:true)!;
        renderer.GetMethod("Create",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[source,docx,pdf]);
        return(File.ReadAllBytes(docx),File.ReadAllBytes(pdf));
    }
}
