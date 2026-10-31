using System.Numerics;
using System.Text.Json;
namespace Magicians.Native;
public class Battle
{
    public List<Fighter> players=new();public string _phase="actual";public string[] styles={"balanced","balanced"};
    public JsonElement?[]? policy_configs;public Dictionary<string,CycleRecord> _economic_visits=new();public NumMap[] cycle_decisions={new(),new()};
    public Battle(string[][] decks,string[] specializations,BigInteger seed,bool trace,string[] styles,string sequence_mode){this.styles=styles;}
    public int cost(Fighter s,Instance x)=>Catalog.CARDS[x.name].cost;
    public int gap(Fighter s)=>50;
    public double rank_card(Fighter s,Instance x)=>0;
    public Dictionary<string,object?> run()=>throw new NotSupportedException("Economy-only oracle does not simulate battles");
}
public class StrictBattle:Battle
{public StrictBattle(string[][] decks,string[] specializations,BigInteger seed,bool trace,string[] styles,string sequence_mode):base(decks,specializations,seed,trace,styles,sequence_mode){}}
