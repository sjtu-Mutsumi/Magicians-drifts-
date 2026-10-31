using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magicians.Native;
namespace Magicians.Tooling;

internal static class GuardRegression
{
    public static int Run(string root)
    {
        var checks=0;
        void Reject(Action action,string label)
        {try{action();}catch(Exception ex)when(ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException or ArgumentException){checks++;return;}throw new InvalidDataException("Guard failed: "+label);}
        var temp=Path.Combine(root,"runtime/csharp-tooling/guard-"+Guid.NewGuid().ToString("N"));
        var release=Path.Combine(temp,"releases/v1.2");var docs=Path.Combine(temp,"docs/cards-v1.2");Directory.CreateDirectory(release);Directory.CreateDirectory(docs);
        var cards=File.ReadAllBytes(Path.Combine(root,"releases/v1.2/cards.json"));var manifest=File.ReadAllBytes(Path.Combine(root,"releases/v1.2/freeze-manifest.json"));
        File.WriteAllBytes(Path.Combine(release,"cards.json"),cards);File.WriteAllBytes(Path.Combine(release,"freeze-manifest.json"),manifest);
        foreach(var e in Catalog.ELEMENTS.Append("generic"))File.Copy(Path.Combine(root,"docs/cards-v1.2",e+".md"),Path.Combine(docs,e+".md"));
        try
        {
            Catalog.Initialize(temp);Catalog.validate_sources();checks++;
            var target=Path.Combine(docs,"fire.md");var original=File.ReadAllText(target);
            var spaced=string.Join('\n',original.Split('\n').Select(l=>l.StartsWith('|')?"|"+string.Join('|',l.Split('|')[1..^1].Select(v=>"  "+v.Trim()+"  "))+"|":l));
            File.WriteAllText(target,spaced,new UTF8Encoding(false));Catalog.validate_sources();checks++;
            foreach(var column in new[]{1,2,4,5})
            {
                var lines=original.Split('\n');var i=Array.FindIndex(lines,l=>l.StartsWith("|引火|",StringComparison.Ordinal));
                if(i<0)throw new InvalidDataException("Expected reference card row");
                var parts=lines[i].Split('|');parts[column+1]=column==5?"changed":"999";lines[i]=string.Join('|',parts);
                File.WriteAllText(target,string.Join('\n',lines),new UTF8Encoding(false));Reject(Catalog.validate_sources,"card source column drift "+column);
            }
            File.WriteAllText(target,original,new UTF8Encoding(false));
            var removed=string.Join('\n',original.Split('\n').Where(l=>!l.StartsWith("|引火|",StringComparison.Ordinal)));
            File.WriteAllText(target,removed,new UTF8Encoding(false));Reject(Catalog.validate_sources,"missing source card");
            var reordered=original.Split('\n');var cardRows=Enumerable.Range(0,reordered.Length).Where(i=>reordered[i].StartsWith('|')&&reordered[i].Split('|').Length>=7&&int.TryParse(reordered[i].Split('|')[2],out _)).ToArray();
            (reordered[cardRows[^1]],reordered[cardRows[^2]])=(reordered[cardRows[^2]],reordered[cardRows[^1]]);
            File.WriteAllText(target,string.Join('\n',reordered),new UTF8Encoding(false));Reject(Catalog.validate_sources,"source iteration order drift");
            File.WriteAllText(target,original,new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(release,"cards.json"),cards.Concat(new byte[]{32}).ToArray());Reject(()=>Catalog.Initialize(temp),"corrupt snapshot checksum");
            File.Delete(Path.Combine(release,"cards.json"));Reject(()=>Catalog.Initialize(temp),"missing card snapshot");
            File.WriteAllBytes(Path.Combine(release,"cards.json"),cards);File.Delete(Path.Combine(release,"freeze-manifest.json"));Reject(()=>Catalog.Initialize(temp),"missing manifest");
        }
        finally{Catalog.Initialize(root);}
        var bundle=Path.Combine(temp,"models");Directory.CreateDirectory(bundle);
        var modelManifest=SixStyles.available_styles();File.Copy(Path.Combine(SixStyles.DefaultBundle,"manifest.json"),Path.Combine(bundle,"manifest.json"));
        var file=modelManifest.GetProperty("models").GetProperty("fire").GetProperty("file").GetString()!;
        var modelPath=Path.Combine(bundle,file);Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);File.Copy(Path.Combine(SixStyles.DefaultBundle,file),modelPath);
        File.AppendAllText(modelPath," ");Reject(()=>SixStyles.load_policy("fire",bundle),"corrupt model hash");
        return checks;
    }
}
