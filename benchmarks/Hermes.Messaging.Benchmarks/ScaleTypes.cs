namespace Hermes.Messaging.Benchmarks;

// 100 distinct closed message types for the type-scaling benchmark. Each must be a unique CLR type
// so that Hermes registers a separate hosted service / store per type.
public static class ScaleTypes
{
    public static readonly Type[] All =
    [
        typeof(S000), typeof(S001), typeof(S002), typeof(S003), typeof(S004), typeof(S005), typeof(S006), typeof(S007), typeof(S008), typeof(S009),
        typeof(S010), typeof(S011), typeof(S012), typeof(S013), typeof(S014), typeof(S015), typeof(S016), typeof(S017), typeof(S018), typeof(S019),
        typeof(S020), typeof(S021), typeof(S022), typeof(S023), typeof(S024), typeof(S025), typeof(S026), typeof(S027), typeof(S028), typeof(S029),
        typeof(S030), typeof(S031), typeof(S032), typeof(S033), typeof(S034), typeof(S035), typeof(S036), typeof(S037), typeof(S038), typeof(S039),
        typeof(S040), typeof(S041), typeof(S042), typeof(S043), typeof(S044), typeof(S045), typeof(S046), typeof(S047), typeof(S048), typeof(S049),
        typeof(S050), typeof(S051), typeof(S052), typeof(S053), typeof(S054), typeof(S055), typeof(S056), typeof(S057), typeof(S058), typeof(S059),
        typeof(S060), typeof(S061), typeof(S062), typeof(S063), typeof(S064), typeof(S065), typeof(S066), typeof(S067), typeof(S068), typeof(S069),
        typeof(S070), typeof(S071), typeof(S072), typeof(S073), typeof(S074), typeof(S075), typeof(S076), typeof(S077), typeof(S078), typeof(S079),
        typeof(S080), typeof(S081), typeof(S082), typeof(S083), typeof(S084), typeof(S085), typeof(S086), typeof(S087), typeof(S088), typeof(S089),
        typeof(S090), typeof(S091), typeof(S092), typeof(S093), typeof(S094), typeof(S095), typeof(S096), typeof(S097), typeof(S098), typeof(S099),
    ];
}

public sealed record S000(int V); public sealed record S001(int V); public sealed record S002(int V); public sealed record S003(int V); public sealed record S004(int V);
public sealed record S005(int V); public sealed record S006(int V); public sealed record S007(int V); public sealed record S008(int V); public sealed record S009(int V);
public sealed record S010(int V); public sealed record S011(int V); public sealed record S012(int V); public sealed record S013(int V); public sealed record S014(int V);
public sealed record S015(int V); public sealed record S016(int V); public sealed record S017(int V); public sealed record S018(int V); public sealed record S019(int V);
public sealed record S020(int V); public sealed record S021(int V); public sealed record S022(int V); public sealed record S023(int V); public sealed record S024(int V);
public sealed record S025(int V); public sealed record S026(int V); public sealed record S027(int V); public sealed record S028(int V); public sealed record S029(int V);
public sealed record S030(int V); public sealed record S031(int V); public sealed record S032(int V); public sealed record S033(int V); public sealed record S034(int V);
public sealed record S035(int V); public sealed record S036(int V); public sealed record S037(int V); public sealed record S038(int V); public sealed record S039(int V);
public sealed record S040(int V); public sealed record S041(int V); public sealed record S042(int V); public sealed record S043(int V); public sealed record S044(int V);
public sealed record S045(int V); public sealed record S046(int V); public sealed record S047(int V); public sealed record S048(int V); public sealed record S049(int V);
public sealed record S050(int V); public sealed record S051(int V); public sealed record S052(int V); public sealed record S053(int V); public sealed record S054(int V);
public sealed record S055(int V); public sealed record S056(int V); public sealed record S057(int V); public sealed record S058(int V); public sealed record S059(int V);
public sealed record S060(int V); public sealed record S061(int V); public sealed record S062(int V); public sealed record S063(int V); public sealed record S064(int V);
public sealed record S065(int V); public sealed record S066(int V); public sealed record S067(int V); public sealed record S068(int V); public sealed record S069(int V);
public sealed record S070(int V); public sealed record S071(int V); public sealed record S072(int V); public sealed record S073(int V); public sealed record S074(int V);
public sealed record S075(int V); public sealed record S076(int V); public sealed record S077(int V); public sealed record S078(int V); public sealed record S079(int V);
public sealed record S080(int V); public sealed record S081(int V); public sealed record S082(int V); public sealed record S083(int V); public sealed record S084(int V);
public sealed record S085(int V); public sealed record S086(int V); public sealed record S087(int V); public sealed record S088(int V); public sealed record S089(int V);
public sealed record S090(int V); public sealed record S091(int V); public sealed record S092(int V); public sealed record S093(int V); public sealed record S094(int V);
public sealed record S095(int V); public sealed record S096(int V); public sealed record S097(int V); public sealed record S098(int V); public sealed record S099(int V);
