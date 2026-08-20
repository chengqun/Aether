using System; using System.Linq; using System.Reflection; using System.Runtime.Loader;
static class P{
 static int Main(){
   var dir="/Users/360jr/.nuget/packages/avalonia/12.1.1/lib/net10.0/";
   foreach(var f in new[]{"Avalonia.dll","Avalonia.Base.dll","Avalonia.Controls.dll"})
     try{ AssemblyLoadContext.Default.LoadFromAssemblyPath(dir+f); }catch(Exception ex){Console.WriteLine("load fail "+f+": "+ex.Message);}
   var all=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name?.StartsWith("Avalonia")==true).ToArray();
   // Window chrome/decorations/extend properties
   var win=all.SelectMany(a=>a.GetTypes()).FirstOrDefault(t=>t.FullName=="Avalonia.Controls.Window");
   Console.WriteLine("== Window: chrome/decoration/extend members ==");
   if(win!=null){
     var ms=win.GetMembers(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static)
       .Where(m=>m.Name.IndexOf("Chrome",StringComparison.OrdinalIgnoreCase)>=0
              || m.Name.IndexOf("Decor",StringComparison.OrdinalIgnoreCase)>=0
              || m.Name.IndexOf("Extend",StringComparison.OrdinalIgnoreCase)>=0
              || m.Name.IndexOf("State",StringComparison.OrdinalIgnoreCase)>=0
              || m.Name.IndexOf("ClientArea",StringComparison.OrdinalIgnoreCase)>=0);
     foreach(var m in ms) Console.WriteLine($"{m.MemberType} {m.Name} :: {(m is PropertyInfo p?p.PropertyType.FullName:(m is EventInfo e?e.EventHandlerType.FullName:""))}");
   } else Console.WriteLine("no Window type");
   // enums with Chrome/Decorations
   Console.WriteLine("== enums ==");
   foreach(var t in all.SelectMany(a=>{try{return a.GetTypes();}catch{return new Type[0];}})
     .Where(t=>t.IsEnum && (t.Name.Contains("Chrome")||t.Name.Contains("Decor")||t.Name.Contains("Extend"))))
     Console.WriteLine(t.FullName+" => "+string.Join(",",Enum.GetNames(t)));
   return 0;
 }
}
