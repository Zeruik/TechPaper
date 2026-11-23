using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TechPaper;
class CitationChecks {
 static void Main(string[] args){
    var m=new Manuscript(args[0]);var accepted=new HashSet<int>();
    foreach(int i in new[]{1,2,3}){
        var p=m.Items[i];var nodes=p.Nodes;
        if(!p.CanChange(nodes.First()) || !p.CanChange(nodes.Last()))throw new Exception("Cannot edit beside anchor");
        if(nodes.Skip(1).Take(nodes.Length-2).Any(p.CanChange))throw new Exception("Anchor is editable");
        p.SetFragment(0,new[]{"","The static citation ","The footnote anchor ","The cross-reference "}[i]);
        p.SetFragment(nodes.Length-1," follows the revised explanation.");
        accepted.Add(i);
    }
    m.Export(args[1],accepted,false);var after=new Manuscript(args[1]);
    if(accepted.Any(i=>m.Items[i].Suggested!=after.Items[i].Text))throw new Exception("Export mismatch");
    Console.WriteLine("PASS 6 real text edits flanking 3 protected anchors");
 }
}
