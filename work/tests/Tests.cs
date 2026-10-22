using FS80H;
using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
static class Tests
{
    static int assertions;
    static void Check(bool condition, string message) { assertions++; if(!condition) throw new Exception(message); }
    static FieldInfo Field(string n) => typeof(MainForm).GetField(n, BindingFlags.NonPublic|BindingFlags.Instance)!;
    static T Get<T>(object form,string n) => (T)form.GetType().GetField(n, BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
    static void Set(MainForm form,string n,object? value) => Field(n).SetValue(form,value);
    static bool Feed(MainForm form,CaptureData? data,uint error=0) => (bool)typeof(MainForm).GetMethod("OnFrame",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[data,error,CancellationToken.None])!;
    static CaptureData Pattern(int shift=0)
    {
        int w=320,h=480; byte[] p=new byte[w*h];
        for(int y=0;y<h;y++) for(int x=0;x<w;x++) p[y*w+x]=(byte)(125+90*Math.Sin((x+shift+.0008*(y-240)*(y-240))*Math.PI/6));
        return new(w,h,p,DateTimeOffset.UtcNow);
    }
    static CaptureData Fixture(string name,int width,int height)
    {
        var raw=File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(),"work","tests","fixtures","gray-"+name+".dat"));
        for(int i=0;i<raw.Length;i++)raw[i]=(byte)(255-raw[i]);
        return new(width,height,raw,DateTimeOffset.UtcNow);
    }
    static IEnumerable<Control> GetAllControls(Control root)
    {
        foreach(Control child in root.Controls){yield return child;foreach(var nested in GetAllControls(child))yield return nested;}
    }
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length>0 && args[0]=="--verify-saved")
        {
            var files=Directory.GetFiles(Directory.GetCurrentDirectory(),"fingerprint_*.zip",SearchOption.AllDirectories);
            var last=files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            using var zip=ZipFile.OpenRead(last);
            using var ms=new MemoryStream(); using(var stream=zip.GetEntry("image.raw")!.Open()) stream.CopyTo(ms);
            byte[] raw=ms.ToArray();
            using var png=zip.GetEntry("image.png")!.Open(); using var bmp=new Bitmap(png);
            int mismatches=0;
            for(int y=0;y<bmp.Height;y++) for(int x=0;x<bmp.Width;x++) {var color=bmp.GetPixel(x,y);byte value=raw[y*bmp.Width+x];if(color.R!=value||color.G!=value||color.B!=value)mismatches++;}
            using var meta=zip.GetEntry("metadata.json")!.Open(); using var json=JsonDocument.Parse(meta);
            var quality=QualityCheck.Evaluate(new(bmp.Width,bmp.Height,raw,DateTimeOffset.UtcNow));
            Console.WriteLine(JsonSerializer.Serialize(new {pngRawMismatchPixels=mismatches,width=bmp.Width,height=bmp.Height,quality.Acceptable,quality.Contrast,quality.TextureFraction,sampleNumber=json.RootElement.GetProperty("enrollment").GetProperty("SampleNumber").GetInt32()}));
            if(mismatches!=0) throw new Exception("PNG mismatch");
            return;
        }
        if(args.Length>0 && args[0]=="--recognition-eval")
        {
            string directory=OutputPreferences.LoadSettings().SaveDirectory;
            var timer=System.Diagnostics.Stopwatch.StartNew();
            var db=FingerprintDatabase.Load(directory,CancellationToken.None,(n,total)=>{if(n%10==0)Console.WriteLine($"Loading {n}/{total}");});
            Console.WriteLine($"Database: {db.Count} images, {db.AliasCount} aliases, skipped {db.Skipped}, {timer.Elapsed.TotalSeconds:F1}s");
            var files=Directory.GetFiles(directory,"fingerprint_*.zip").OrderByDescending(File.GetLastWriteTimeUtc).Take(5).ToArray();
            foreach(string file in files)
            {
                using var zip=ZipFile.OpenRead(file);using var metadata=zip.GetEntry("metadata.json")!.Open();using var doc=JsonDocument.Parse(metadata);
                var m=doc.RootElement;string expected=m.GetProperty("alias").GetString()!;
                using var ms=new MemoryStream();using(var raw=zip.GetEntry("image.raw")!.Open())raw.CopyTo(ms);
                byte[] pixels=ms.ToArray();
                try{var result=db.Identify(new(m.GetProperty("width").GetInt32(),m.GetProperty("height").GetInt32(),pixels,DateTimeOffset.UtcNow),CancellationToken.None,file);Console.WriteLine($"Held-out image: found={result.Found}, expectedAlias={result.Alias==expected}, score={result.Score:F1}, ambiguous={result.Ambiguous}");}
                finally{Array.Clear(pixels);}
            }
            var blank=db.Identify(new(320,480,new byte[320*480],DateTimeOffset.UtcNow),CancellationToken.None);
            Console.WriteLine($"Blank: found={blank.Found}, score={blank.Score:F1}");
            return;
        }
        var writes=new List<bool>();
        using(var light=new ScannerIndicator(writes.Add))
        {
            Check(writes.SequenceEqual([true]),"LED starts ON");
            light.Update(false,1);Check(writes.Count==1,"LED steady during scan");
            light.Update(true,10);light.Update(true,10.49);Check(writes.Last(),"wait starts ON");
            light.Update(true,10.5);Check(!writes.Last(),"wait OFF half cycle");
            light.Update(true,11);Check(writes.Last(),"wait ON next half cycle");
            light.Update(false,11.6);light.Update(false,12.6);Check(writes.Last(),"scanning exits blinking");
        }
        Check(!writes.Last(),"LED off after normal disposal");
        try {using var light=new ScannerIndicator(writes.Add);throw new OperationCanceledException();}catch(OperationCanceledException){}
        Check(!writes.Last(),"LED off on cancellation unwind");
        int ledWarnings=0,ledAttempts=0;
        using(var broken=new ScannerIndicator(on=>{ledAttempts++;throw new Exception("LED unavailable");},_=>ledWarnings++)) {broken.Update(true,3);}
        Check(ledWarnings==1 && ledAttempts==2,"LED failure reported once and OFF still attempted");
        using(var brokenUi=new ScannerIndicator(_=>throw new Exception("no LED"),_=>throw new Exception("notification failed"))) {}
        var strideData=new CaptureData(3,2,new byte[]{0,20,40,60,80,100},DateTimeOffset.UtcNow);
        using(var canvas=PreviewImage.Create(strideData))
        {
            var next=strideData with {Pixels=new byte[]{255,210,170,130,90,50}};
            PreviewImage.Update(canvas,next);
            for(int y=0;y<2;y++)for(int x=0;x<3;x++)Check(canvas.GetPixel(x,y).R==next.Pixels[y*3+x],"updated preview pixels and row stride");
            try{PreviewImage.Update(canvas,next with {Width=2});throw new Exception("dimension failure expected");}catch(ArgumentException){assertions++;}
            Check(canvas.GetPixel(2,1).R==50,"bad update does not corrupt bitmap");
        }
        var data=Pattern();
        Check(QualityCheck.Evaluate(data).Acceptable,"ridge pattern accepted");
        Check(!QualityCheck.Evaluate(data with{Pixels=new byte[data.Pixels.Length]}).Acceptable,"blank rejected");
        Check(!QualityCheck.Evaluate(data with{Pixels=Enumerable.Repeat((byte)255,data.Pixels.Length).ToArray()}).Acceptable,"white rejected");
        var tracker=new StabilityTracker();
        for(int i=0;i<10;i++) Check(!tracker.Update(data.Pixels,true,i*.2),"no premature auto");
        Check(tracker.Update(data.Pixels,true,2),"2 second stability");
        var moved=Pattern(3); Check(!tracker.Update(moved.Pixels,true,2.2),"motion reset");
        Check(tracker.Seconds==0,"timer zero after motion");
        Check(!tracker.Update(moved.Pixels,true,4),"gap reset");
        tracker.Update(moved.Pixels,false,4.1); Check(tracker.Seconds==0,"bad quality reset");
        tracker.Reset();
        // Slow accumulated drift must not pass simply because adjacent frames are close.
        for(int i=0;i<15;i++) Check(!tracker.Update(data.Pixels.Select(v=>(byte)Math.Clamp(v+i*2,0,255)).ToArray(),true,i*.2),"anchor detects slow drift");
        var variants=new VariantTracker(); variants.Add(data);
        Check(!variants.IsDistinct(data),"duplicate rejected"); Check(variants.IsDistinct(moved),"shifted variant accepted"); variants.Clear();
        string dir=Path.Combine(Path.GetTempPath(),"FS80H-check-"+Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string settings=Path.Combine(dir,"prefs","settings.json");
            Check(OutputPreferences.Load(settings)=="","missing settings safe");
            OutputPreferences.Save(dir,settings); Check(OutputPreferences.Load(settings)==dir,"folder persists");
            Check(JsonDocument.Parse(File.ReadAllText(settings)).RootElement.EnumerateObject().Count()==6,"only requested preferences stored");
            File.WriteAllText(settings,"invalid"); Check(OutputPreferences.Load(settings)=="","corrupt settings safe");
            File.Delete(settings); Directory.Delete(Path.GetDirectoryName(settings)!);
            OutputPreferences.SaveSettings(new AppPreferences(dir,"Учебный псевдоним",false,false,7),settings);
            var restored=OutputPreferences.LoadSettings(settings);
            Check(restored.Alias=="Учебный псевдоним" && !restored.Auto && !restored.Series && restored.Samples==7,"all preferences roundtrip");
            OutputPreferences.Save(dir,settings); Check(OutputPreferences.LoadSettings(settings).Alias=="Учебный псевдоним","folder change preserves alias");
            File.WriteAllText(settings,"{\"SaveDirectory\":\"C:\\\\data\"}");
            Check(OutputPreferences.LoadSettings(settings).Auto && OutputPreferences.LoadSettings(settings).Samples==5,"legacy settings defaults");
            Directory.Delete(Path.GetDirectoryName(settings)!,true);
            var info=new CaptureInfo("synthetic-test",1,2,"center","manual",0);
            string path=Storage.Save(dir,"synthetic-test",data,info);
            string path2=Storage.Save(dir,"synthetic-test",data,info);
            Check(path!=path2,"unique filenames");
            using(var zip=ZipFile.OpenRead(path))
            {
                Check(zip.Entries.Count==3,"3 entries");
                using var ms=new MemoryStream(); using(var r=zip.GetEntry("image.raw")!.Open()) r.CopyTo(ms);
                Check(ms.ToArray().SequenceEqual(data.Pixels),"raw exact");
                using var png=zip.GetEntry("image.png")!.Open(); using var bmp=new Bitmap(png);
                Check(bmp.Width==320&&bmp.Height==480,"PNG size");
                Check(bmp.GetPixel(100,200).R==data.Pixels[200*320+100],"PNG equals raw");
                using var meta=zip.GetEntry("metadata.json")!.Open(); using var json=JsonDocument.Parse(meta);
                Check(json.RootElement.GetProperty("schemaVersion").GetInt32()==3,"schema3");
                Check(json.RootElement.GetProperty("alias").GetString()=="synthetic-test","alias metadata");
                Check(!json.RootElement.TryGetProperty("fingerNumber",out _),"no finger metadata");
                Check(json.RootElement.GetProperty("enrollment").GetProperty("SessionId").GetString()=="synthetic-test","session metadata");
            }
            using var form=new MainForm(Path.Combine(dir,"prefs-ui","settings.json")); using var cts=new CancellationTokenSource();
            Check(Get<AppPreferences>(form,"preferences").Auto,"auto enabled after startup");
            Check(Get<Label>(form,"quality").Text=="Готово","short initial label");
            Set(form,"preferences",Get<AppPreferences>(form,"preferences") with { Auto=false });
            Set(form,"cts",cts); Set(form,"directory",dir); Set(form,"sessionUser","synthetic-test"); Set(form,"sessionId","test-session");  Set(form,"requested",2);
            Check(Feed(form,data),"manual live");
            var firstPreview=Get<PictureBox>(form,"preview").Image;Feed(form,data);
            Check(ReferenceEquals(firstPreview,Get<PictureBox>(form,"preview").Image),"live preview reuses image buffer");Check(Get<Label>(form,"quality").Text=="Сканирование","short scanning label"); Check(Get<Button>(form,"ok").Enabled,"manual OK enabled");
            Check(Directory.GetFiles(dir).Length==2,"preview does not save");
            Get<Button>(form,"ok").PerformClick();
            // Hidden Forms do not execute PerformClick; invoke the same handler directly.
            if(Get<int>(form,"completed")==0) typeof(MainForm).GetMethod("SaveCurrent",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[false]);
            Check(Get<int>(form,"completed")==1,"manual save");Check(Get<Label>(form,"quality").Text=="Ожидание","short waiting label");
            Feed(form,moved); Check(Get<int>(form,"completed")==1&&!Get<Button>(form,"ok").Enabled,"must lift after save");
            Feed(form,null,0x20000001); Check(Get<bool>(form,"waitingForLift"),"movement is not release");
            Feed(form,null,4306); Check(Get<bool>(form,"waitingForLift"),"brief empty insufficient");
            Set(form,"absentSince",StabilityTracker.Now-.5); Feed(form,null,4306); Check(!Get<bool>(form,"waitingForLift"),"release rearms");
            Feed(form,data); Check(!Get<Button>(form,"ok").Enabled,"duplicate blocked");
            Feed(form,moved); Check(Get<Button>(form,"ok").Enabled,"new variant enabled");
            Feed(form,null,0x20000002); Check(!Get<Button>(form,"ok").Enabled,"lost frame clears OK");
            Set(form,"preferences",Get<AppPreferences>(form,"preferences") with { Auto=true });
            var st=Get<StabilityTracker>(form,"stability"); double now=StabilityTracker.Now;
            for(int i=0;i<6;i++) st.Update(moved.Pixels,true,now-2.1+i*.4);
            Check(!Feed(form,moved),"auto completes series"); Check(Get<int>(form,"completed")==2,"auto saved once");
            Feed(form,moved); Check(Get<int>(form,"completed")==2,"no repeated auto save");
            Set(form,"cts",null);
            string fullDir=Path.Combine(dir,"full-series");Directory.CreateDirectory(fullDir);
            using var fullForm=new MainForm(Path.Combine(dir,"prefs-full","settings.json")); using var fullCts=new CancellationTokenSource();
            Check(Get<AppPreferences>(fullForm,"preferences").Auto,"fresh form auto default");
            Check(Get<AppPreferences>(fullForm,"preferences").Series && Get<AppPreferences>(fullForm,"preferences").Samples==5,"fresh form five samples");
            Check(Get<Button>(fullForm,"setup").AccessibleName!.Contains("Setup"),"setup accessible");
            Set(fullForm,"cts",fullCts);Set(fullForm,"directory",fullDir);Set(fullForm,"sessionUser","synthetic-five");Set(fullForm,"sessionId","five-sample-session");Set(fullForm,"requested",5);
            for(int sample=0;sample<5;sample++)
            {
                var variant=Pattern(sample);
                int before=Directory.GetFiles(fullDir).Length;
                Feed(fullForm,variant);
                Check(Directory.GetFiles(fullDir).Length==before,"no immediate save");
                var watch=System.Diagnostics.Stopwatch.StartNew();
                while(Get<int>(fullForm,"completed")==sample && watch.Elapsed.TotalSeconds<3.5) { Thread.Sleep(100);Feed(fullForm,variant); }
                Check(Get<int>(fullForm,"completed")==sample+1,$"auto sample {sample+1} saved");
                for(int repeat=0;repeat<3;repeat++) Feed(fullForm,variant);
                Check(Directory.GetFiles(fullDir).Length==sample+1,"held finger no repeats");
                if(sample<4) { Feed(fullForm,null,4306);Thread.Sleep(450);Feed(fullForm,null,4306);Check(!Get<bool>(fullForm,"waitingForLift"),"release advances series"); }
            }
            Check(Get<bool>(fullForm,"done"),"full series done");Check(Get<Label>(fullForm,"quality").Text=="Готово","short completed label");
            var numbers=new HashSet<int>();
            foreach(var file in Directory.GetFiles(fullDir))
            {
                using var z=ZipFile.OpenRead(file);using var m=z.GetEntry("metadata.json")!.Open();using var doc=JsonDocument.Parse(m);
                var e=doc.RootElement.GetProperty("enrollment");
                Check(e.GetProperty("SaveMode").GetString()=="auto","auto metadata");
                Check(e.GetProperty("StableSeconds").GetDouble()>=2,"stable two seconds metadata");
                Check(e.GetProperty("RequestedSamples").GetInt32()==5,"five requested metadata");
                Check(e.GetProperty("SessionId").GetString()=="five-sample-session","one session metadata");
                numbers.Add(e.GetProperty("SampleNumber").GetInt32());
                foreach(var entry in z.Entries) {using var stream=entry.Open();stream.CopyTo(Stream.Null);}
            }
            Check(numbers.SetEquals([1,2,3,4,5]),"all sample numbers present");
            Set(fullForm,"cts",null);
            Check(!FingerprintDatabase.Decide(new Dictionary<string,double>()).Found,"empty scores not found");
            var borderline=new Dictionary<string,double>{{"known",35}};
            Check(!FingerprintDatabase.Decide(borderline,40).Found,"default rejects borderline score");
            Check(FingerprintDatabase.Decide(borderline,35).Found,"softer threshold accepts borderline score");
            Check(!FingerprintDatabase.Decide(borderline,50).Found,"strict threshold rejects borderline score");
            Check(FingerprintDatabase.Decide(borderline,-100).Threshold==30,"engine lower bound");
            Check(FingerprintDatabase.Decide(borderline,500).Threshold==60,"engine upper bound");
            Check(FingerprintDatabase.Decide(borderline,double.NaN).Threshold==40,"invalid threshold defaults");
            Check(FingerprintDatabase.Decide(new Dictionary<string,double>{{"a",39},{"b",38}},30).Ambiguous,"soft mode retains ambiguity rejection");
            Check(!FingerprintDatabase.Decide(new Dictionary<string,double>{{"a",39.9}}).Found,"threshold enforced");
            Check(FingerprintDatabase.Decide(new Dictionary<string,double>{{"a",40}}).Alias=="a","threshold boundary");
            Check(FingerprintDatabase.Decide(new Dictionary<string,double>{{"a",70},{"b",69}}).Ambiguous,"ambiguous aliases rejected");
            Check(!FingerprintDatabase.Decide(new Dictionary<string,double>{{"a",double.NaN}}).Found,"invalid score ignored");
            var fixtureProbe=Fixture("probe",332,533);var fixtureMatch=Fixture("matching",320,407);var fixtureOther=Fixture("nonmatching",333,435);
            string recognitionDir=Path.Combine(dir,"recognition");Directory.CreateDirectory(recognitionDir);
            Storage.Save(recognitionDir,"public-fixture-A",fixtureMatch);
            File.WriteAllText(Path.Combine(recognitionDir,"fingerprint_broken.zip"),"invalid zip");
            var fixtureDb=FingerprintDatabase.Load(recognitionDir,CancellationToken.None);
            Check(fixtureDb.Count==1&&fixtureDb.Skipped==1,"invalid archive skipped");
            int beforeRecognition=Directory.GetFiles(recognitionDir).Length;
            var positive=fixtureDb.Identify(fixtureProbe,CancellationToken.None);
            Check(positive.Found&&positive.Alias=="public-fixture-A"&&positive.Score>=40,"independent matching fingerprint recognized");
            var negative=fixtureDb.Identify(fixtureOther,CancellationToken.None);
            Console.WriteLine($"Fixture positive={positive.Score:F2}, negative={negative.Score:F2}, found={negative.Found}, displayLength={negative.Display.Length}");
            Check(!negative.Found&&negative.Display=="Не найден","different fingerprint not found");
            Check(!fixtureDb.Identify(new(320,480,new byte[320*480],DateTimeOffset.UtcNow),CancellationToken.None).Found,"blank not recognized");
            Check(Directory.GetFiles(recognitionDir).Length==beforeRecognition,"recognition writes no images/templates");
            try{fixtureDb.Identify(fixtureProbe,new CancellationToken(true));throw new Exception("cancel expected");}catch(OperationCanceledException){assertions++;}
            using var recForm=new MainForm(Path.Combine(dir,"rec-prefs","settings.json"));
            Get<TextBox>(recForm,"user").Text="registration alias unrelated to result";
            typeof(MainForm).GetMethod("ShowRecognitionResult",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[positive]);
            Check(Get<Label>(recForm,"hint").Text=="public-fixture-A","UI shows matched alias");
            Check(Get<TextBox>(recForm,"user").Text=="registration alias unrelated to result","recognition does not replace registration alias");
            typeof(MainForm).GetMethod("ShowRecognitionResult",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[negative]);
            Check(Get<Label>(recForm,"hint").Text=="Не найден","UI shows not found");
            var gate=new RecognitionTouchGate();
            Check(gate.AcceptFrame(true,0,0),"first touch accepted without button");gate.RequireLift();
            for(int i=0;i<10;i++)Check(!gate.AcceptFrame(true,0,i*.2),"held finger never rearms");
            gate.AcceptFrame(false,0x20000001,3);Check(gate.WaitingForLift,"motion not release");
            gate.AcceptFrame(false,0x20000002,4);Check(gate.WaitingForLift,"no frame not release");
            gate.AcceptFrame(false,4306,5);Check(gate.WaitingForLift,"short empty not release");
            gate.AcceptFrame(false,4306,5.5);Check(!gate.WaitingForLift,"empty rearms next touch");
            Check(gate.AcceptFrame(true,0,5.6),"second touch accepted");
            gate.Reset(true);Check(gate.WaitingForLift,"registration can require lift");gate.Reset();
            Get<RecognitionTouchGate>(recForm,"touchGate").RequireLift();
            int automaticCaptures=0;var automaticCallback=(Action<CaptureData>)(_=>automaticCaptures++);
            for(int i=0;i<3;i++)typeof(MainForm).GetMethod("OnAutomaticFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[data,(uint)0,CancellationToken.None,automaticCallback]);
            Check(automaticCaptures==0,"automatic UI waits for lift after result");
            Get<RecognitionTouchGate>(recForm,"touchGate").Reset();
            var autoTracker=Get<StabilityTracker>(recForm,"stability");double autoNow=StabilityTracker.Now;
            for(int i=0;i<6;i++)autoTracker.Update(data.Pixels,true,autoNow-2.1+i*.4);
            var autoReady=(Action<CaptureData>)(c=>{automaticCaptures++;Array.Clear(c.Pixels);});
            bool autoKeep=(bool)typeof(MainForm).GetMethod("OnAutomaticFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[data,(uint)0,CancellationToken.None,autoReady])!;
            Check(!autoKeep&&automaticCaptures==1,"automatic touch captures without a button");
            Get<RecognitionTouchGate>(recForm,"touchGate").RequireLift();
            typeof(MainForm).GetMethod("OnAutomaticFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[null,(uint)4306,CancellationToken.None,autoReady]);Thread.Sleep(450);
            typeof(MainForm).GetMethod("OnAutomaticFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[null,(uint)4306,CancellationToken.None,autoReady]);
            autoNow=StabilityTracker.Now;for(int i=0;i<6;i++)autoTracker.Update(data.Pixels,true,autoNow-2.1+i*.4);
            autoKeep=(bool)typeof(MainForm).GetMethod("OnAutomaticFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[data,(uint)0,CancellationToken.None,autoReady])!;
            Check(!autoKeep&&automaticCaptures==2,"next automatic touch captures after lift");
            Check(Directory.GetFiles(recognitionDir).Length==beforeRecognition,"automatic loop writes no captures");
            autoTracker.Reset();
            var recTracker=Get<StabilityTracker>(recForm,"stability");double recNow=StabilityTracker.Now;
            for(int i=0;i<6;i++)recTracker.Update(data.Pixels,true,recNow-2.1+i*.4);
            CaptureData? captured=null;
            var recCallback=(Action<CaptureData>)(c=>captured=c);
            bool keepReading=(bool)typeof(MainForm).GetMethod("OnRecognitionFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(recForm,[data,(uint)0,CancellationToken.None,recCallback])!;
            Check(!keepReading&&captured!=null,"recognition accepts stable frame without OK");
            Check(!ReferenceEquals(captured!.Pixels,data.Pixels),"recognition owns copy of native buffer");Array.Clear(captured.Pixels);
            Check(Directory.GetFiles(recognitionDir).Length==beforeRecognition,"recognition callback saves no files");

            Get<TextBox>(fullForm,"user").Text="Псевдоним после перезапуска";
            typeof(MainForm).GetMethod("SavePreferences",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(fullForm,[]);
            using var reopened=new MainForm(Path.Combine(dir,"prefs-full","settings.json"));
            Check(Get<TextBox>(reopened,"user").Text=="Псевдоним после перезапуска","alias restored in new form");
            using var setupForm=new SetupForm(new AppPreferences(dir,"unchanged alias",false,false,8));
            Check(setupForm.Result.Alias=="unchanged alias","setup retains alias");
            Get<TrackBar>(setupForm,"recognitionThreshold").Value=35;
            Check(!GetAllControls(reopened).OfType<TrackBar>().Any(),"threshold removed from main window");
            Check(!GetAllControls(reopened).OfType<Button>().Any(b=>b.Text=="Распознать"),"recognize button removed");
            using var thresholdDialog=new SetupForm(new AppPreferences(dir,RecognitionThreshold:35));
            Check(Get<TrackBar>(thresholdDialog,"recognitionThreshold").Value==35,"threshold restored in Setup");
            var resetThreshold=GetAllControls(thresholdDialog).OfType<Button>().Single(b=>b.AccessibleName=="Сбросить порог к 40");
            typeof(Control).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(resetThreshold,[EventArgs.Empty]);
            Check(Get<TrackBar>(thresholdDialog,"recognitionThreshold").Value==40,"Setup reset restores default");
            Check(Get<TrackBar>(thresholdDialog,"recognitionThreshold").Minimum==30&&Get<TrackBar>(thresholdDialog,"recognitionThreshold").Maximum==60,"Setup slider bounds");
            var setupAuto=GetAllControls(setupForm).OfType<CheckBox>().Single(c=>c.Text.StartsWith("Авто"));
            var setupSeries=GetAllControls(setupForm).OfType<CheckBox>().Single(c=>c.Text.StartsWith("Собирать"));
            setupAuto.Checked=true;setupSeries.Checked=true;
            GetAllControls(setupForm).OfType<NumericUpDown>().Single().Value=6;
            setupForm.PerformLayout(); foreach(var control in GetAllControls(setupForm)) control.PerformLayout();
            var saveButton=GetAllControls(setupForm).OfType<Button>().Single(c=>c.Text=="Сохранить");
            Check(saveButton.Parent!.Bottom<=saveButton.Parent.Parent!.ClientSize.Height,"setup buttons fit dialog");
            typeof(Control).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(saveButton,[EventArgs.Empty]);
            Check(setupForm.DialogResult==DialogResult.OK && setupForm.Result.Auto && setupForm.Result.Series && setupForm.Result.Samples==6,"setup saves edited values");
            Check(setupForm.Result.Alias=="unchanged alias","setup editing preserves alias");
            Check(setupForm.Result.RecognitionThreshold==35,"Setup applies threshold");
            OutputPreferences.SaveSettings(setupForm.Result,Path.Combine(dir,"setup-threshold","settings.json"));
            using var thresholdRoundTrip=new SetupForm(OutputPreferences.LoadSettings(Path.Combine(dir,"setup-threshold","settings.json")));
            Check(Get<TrackBar>(thresholdRoundTrip,"recognitionThreshold").Value==35,"Setup threshold persists");
            Get<TextBox>(reopened,"user").Text="Закрытие сохраняет псевдоним";
            typeof(Form).GetMethod("OnFormClosing",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(reopened,[new FormClosingEventArgs(CloseReason.UserClosing,false)]);
            using var reopenedAgain=new MainForm(Path.Combine(dir,"prefs-full","settings.json"));
            Check(Get<TextBox>(reopenedAgain,"user").Text=="Закрытие сохраняет псевдоним","closing flushes alias without debounce wait");
            Check(!GetAllControls(reopened).Any(c=>c.AccessibleName=="Номер пальца"),"no finger control");
            Check(!GetAllControls(reopened).OfType<CheckBox>().Any(),"capture settings moved off main form");
            try { Scanner.Stream(CancellationToken.None,(_,_)=>false); throw new Exception("missing DLL expected"); } catch(InvalidOperationException e) when(e.Message.Contains("ftrScanAPI.dll")) { assertions++; }
            Console.WriteLine($"PASS: {assertions} assertions. Synthetic and public upstream fixtures; no device capture.");
        }
        finally { Directory.Delete(dir,true); }
    }
}
