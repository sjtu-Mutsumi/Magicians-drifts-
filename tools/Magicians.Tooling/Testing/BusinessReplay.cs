using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Magicians.Native;

namespace Magicians.Tooling;

internal sealed class ReplayBattle : StrictBattle
{
    public bool SuppressWave;
    public List<Queue<string>>? Planned;
    public ReplayBattle(IEnumerable<IEnumerable<string>> decks) : base(decks, seed:19, trace:true) { }
    public override Instance? choose_play(int seat)
    {
        if(Planned is null) return base.choose_play(seat);
        var q=Planned[seat];
        if(q.Count==0)return null;var uid=q.Dequeue();return players[seat].hand.Single(x=>x.uid==uid);
    }
    public override void wave(List<Packet> packets,bool reaction=false)
    { if(!SuppressWave)base.wave(packets,reaction); }
}

/// <summary>Replays immutable original-test operations, including their pre-state and RNG.</summary>
internal static class BusinessReplay
{
    public static void Execute(JsonElement row)
    {
        var before=row.GetProperty("before");
        var b=Create(before);
        object? result;
        var method=row.GetProperty("method").GetString()!;
        if(method=="resolve_sequences")
        {
            var plan=row.GetProperty("plan");
            Restore(b,plan.GetProperty("before"));
            b.Planned=plan.GetProperty("uids").EnumerateArray().Select(a=>new Queue<string>(a.EnumerateArray().Select(x=>x.GetString()!))).ToList();
            var sequence=b.plan_stage();
            b.Planned=null;
            Restore(b,before);
            result=b.resolve_sequences(sequence);
        }
        else if(method=="$effect")
        {
            var s=row.GetProperty("before_effect");
            int I(string n,int fallback=0)=>s.TryGetProperty(n,out var v)?v.GetInt32():fallback;
            var pre=new Before(I("hp",1000),I("shield"),I("total"),s.TryGetProperty("defense",out var df)?(Q)ConvertValue(df,typeof(Q),b)!:40,
                I("spent"),I("played"),I("fire"),I("wind"),s.TryGetProperty("first",out var fst)&&fst.GetBoolean());
            Effects.Apply(b,row.GetProperty("actor").GetInt32(),new Instance("effect",row.GetProperty("name").GetString()!),pre);
            result=null;
        }
        else
        {
            b.SuppressWave=row.TryGetProperty("suppress_wave",out var sw)&&sw.GetBoolean();
            var mi=typeof(ReplayBattle).GetMethods().SingleOrDefault(m=>m.Name==method && !m.IsGenericMethod)
                ?? throw new InvalidOperationException("No native method: "+method);
            var positional=row.GetProperty("args").EnumerateArray().ToArray();
            var named=row.GetProperty("kwargs");
            var parameters=mi.GetParameters();
            var values=new object?[parameters.Length];
            for(var i=0;i<parameters.Length;i++)
            {
                var p=parameters[i];
                if(i<positional.Length) values[i]=ConvertValue(positional[i],p.ParameterType,b);
                else if(named.TryGetProperty(p.Name!,out var n))values[i]=ConvertValue(n,p.ParameterType,b);
                else if(p.HasDefaultValue)values[i]=p.DefaultValue;
                else throw new InvalidOperationException("Missing argument: "+method+"/"+p.Name);
            }
            try {result=mi.Invoke(b,values);}catch(TargetInvocationException ex){throw ex.InnerException!;}
        }
        var actual=ReferenceFixtures.Snapshot(b);
        // Stored styles/configs are restoration inputs, not output side effects.
        var expected=row.GetProperty("after");
        TestCommands.Compare(expected,NativeJson.Element(actual),"after",ignoreStateMetadata:true);
        if(method!="resolve_sequences")TestCommands.Compare(row.GetProperty("result"),NativeJson.Element(Normalize(result)),"result");
    }

    private static object? Normalize(object? value)=>value switch
    {
        Instance x=>NativeJson.Obj(("uid",x.uid),("name",x.name)),
        Modifier m=>NativeJson.Obj(("kind",m.kind),("value",m.value),("owner",m.owner),("expires",m.expires),("stamp",m.stamp??Array.Empty<object>())),
        IEnumerable<Instance> xs=>xs.Select(Normalize).ToArray(),
        _=>value
    };
    private static ReplayBattle Create(JsonElement state)
    {
        var decks=state.GetProperty("players").EnumerateArray().Select(s=>s.GetProperty("deck").EnumerateArray().Select(x=>x.GetString()!).ToArray()).ToArray();
        var b=new ReplayBattle(decks);Restore(b,state);return b;
    }
    private static void Restore(ReplayBattle b,JsonElement state)
    {
        b.players=state.GetProperty("players").EnumerateArray().Select(s=>RestoreFighter(s,b)).ToList();
        b.time=state.GetProperty("time").GetInt64();
        b.log=state.GetProperty("log").EnumerateArray().Select(x=>(Dictionary<string,object?>)ConvertValue(x,typeof(Dictionary<string,object?>),b)!).ToList();
        b.operations=state.GetProperty("operations").EnumerateArray().Select(x=>(Operation)ConvertValue(x,typeof(Operation),b)!).ToList();
        foreach(var name in new[]{"executed","sequence_skips"})typeof(Battle).GetField(name)!.SetValue(b,ConvertValue(state.GetProperty(name),typeof(NumMap),b));
        foreach(var name in new[]{"executed_by_seat","flow_by_seat","cycle_decisions"})typeof(Battle).GetField(name)!.SetValue(b,ConvertValue(state.GetProperty(name),typeof(NumMap[]),b));
        b.planned_count=state.GetProperty("planned_count").GetInt32();
        if(state.TryGetProperty("styles",out var styles))b.styles=styles.EnumerateArray().Select(x=>x.GetString()!).ToArray();
        if(state.TryGetProperty("policy_configs",out var configs)&&configs.ValueKind!=JsonValueKind.Null)b.policy_configs=configs.EnumerateArray().Select(x=>(JsonElement?)x.Clone()).ToArray();
        b._phase="actual";
        if(state.TryGetProperty("trace",out var trace))b.trace=trace.GetBoolean();
        if(state.TryGetProperty("projecting",out var projecting))b.projecting=projecting.GetBoolean();
    }
    private static Fighter RestoreFighter(JsonElement value,ReplayBattle b)
    {
        var fighter=new Fighter(value.GetProperty("seat").GetInt32(),value.GetProperty("deck").EnumerateArray().Select(x=>x.GetString()!));
        foreach(var property in value.EnumerateObject())
        {
            if(property.Name=="rng")
            {
                var data=property.Value[1].EnumerateArray().ToArray();
                fighter.rng.setstate(new PythonRandom.State(data[..^1].Select(x=>x.GetUInt32()).ToArray(),data[^1].GetInt32()));continue;
            }
            if(property.Name=="fx")
            {
                foreach(var v in property.Value.EnumerateObject())
                    if(v.Name=="strip")fighter.strips=(List<(Q,int)>)ConvertValue(v.Value,typeof(List<(Q,int)>),b)!;
                    else fighter.fx[v.Name]=(Q)ConvertValue(v.Value,typeof(Q),b)!;
                continue;
            }
            if(property.Name=="pending")
            {
                foreach(var v in property.Value.EnumerateObject())
                    if(v.Name=="heal_batches")fighter.pending.heal_batches=(List<Q>)ConvertValue(v.Value,typeof(List<Q>),b)!;
                    else fighter.pending[v.Name]=(Q)ConvertValue(v.Value,typeof(Q),b)!;
                continue;
            }
            var field=typeof(Fighter).GetField(property.Name)??throw new InvalidDataException("Unknown fighter field "+property.Name);
            field.SetValue(fighter,ConvertValue(property.Value,field.FieldType,b));
        }
        return fighter;
    }

    private static object? ConvertValue(JsonElement value,Type type,ReplayBattle b)
    {
        if(value.ValueKind==JsonValueKind.Null)return type.IsArray?Array.CreateInstance(type.GetElementType()!,0):null;
        if(type==typeof(JsonElement))return value.Clone();
        if(Nullable.GetUnderlyingType(type) is {} nullable)return ConvertValue(value,nullable,b);
        if(type==typeof(Q))return Q.Parse(value.ValueKind==JsonValueKind.String?value.GetString()!:value.GetRawText());
        if(type==typeof(BigInteger))return BigInteger.Parse(value.GetRawText());
        if(type==typeof(int))return value.GetInt32();
        if(type==typeof(long))return value.GetInt64();
        if(type==typeof(bool))return value.GetBoolean();
        if(type==typeof(string))return value.GetString();
        if(type==typeof(Fighter))return b.players[value.GetProperty("$fighter").GetInt32()];
        if(type==typeof(Func<Card,bool>))
        {var names=value.GetProperty("$predicate").EnumerateArray().Select(x=>x.GetString()!).ToHashSet();return (Func<Card,bool>)(card=>names.Contains(card.name));}
        if(type==typeof(Instance))return new Instance(value.GetProperty("uid").GetString()!,value.GetProperty("name").GetString()!);
        if(type==typeof(Modifier))return new Modifier(value.GetProperty("kind").GetString()!,(Q)ConvertValue(value.GetProperty("value"),typeof(Q),b)!,value.GetProperty("owner").GetInt32(),value.GetProperty("expires").GetInt64(),(object[])ConvertValue(value.GetProperty("stamp"),typeof(object[]),b)!);
        if(type==typeof(Operation))
        {
            var kind=value[0].GetString()!;var v=value[2];
            object val=kind switch {
                "packet"=>ConvertValue(v,typeof(Packet),b)!,"modifier"=>ConvertValue(v,typeof(Modifier),b)!,
                "poison"=>(v[0].GetInt32(),v[1].GetInt32()),"def_down_permanent"=>ConvertValue(v,typeof(Q),b)!,_=>v.GetInt32()};
            return new Operation(kind,value[1].GetInt32(),val);
        }
        if(type==typeof(object))return value.ValueKind switch
        {
            JsonValueKind.Object=>ConvertValue(value,typeof(Dictionary<string,object?>),b),
            JsonValueKind.Array=>ConvertValue(value,typeof(object[]),b),
            JsonValueKind.True=>true,JsonValueKind.False=>false,
            JsonValueKind.String=>NumericString(value.GetString()!),
            _=>Q.Parse(value.GetRawText())
        };
        if(type.IsArray)
        {var rows=value.EnumerateArray().ToArray();var array=Array.CreateInstance(type.GetElementType()!,rows.Length);for(var i=0;i<rows.Length;i++)array.SetValue(ConvertValue(rows[i],type.GetElementType()!,b),i);return array;}
        if(type.IsGenericType&&type.FullName!.StartsWith("System.ValueTuple",StringComparison.Ordinal))
        {var types=type.GetGenericArguments();return Activator.CreateInstance(type,types.Select((t,i)=>ConvertValue(value[i],t,b)).ToArray());}
        if(typeof(IDictionary).IsAssignableFrom(type))
        {
            var map=(IDictionary)Activator.CreateInstance(type)!;
            var types=type.BaseType?.IsGenericType==true?type.BaseType.GetGenericArguments():type.GetGenericArguments();
            if(type.IsGenericType)types=type.GetGenericArguments();
            foreach(var p in value.EnumerateObject())map.Add(p.Name,ConvertValue(p.Value,types[1],b));
            return map;
        }
        if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(List<>))
        {var list=(IList)Activator.CreateInstance(type)!;foreach(var row in value.EnumerateArray())list.Add(ConvertValue(row,type.GetGenericArguments()[0],b));return list;}
        if(type==typeof(HashSet<string>))return value.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
        throw new NotSupportedException("Cannot restore "+type.FullName);
    }
    private static object NumericString(string value)
    {try{return Q.Parse(value);}catch(FormatException){return value;}}
}
