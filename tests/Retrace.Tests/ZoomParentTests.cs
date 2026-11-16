using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

/// <summary>The rule that resolves the newest created object to the object that sits in model space (<see cref="ZoomResolver{T}"/>, <see cref="ZoomParent"/>), with a fake drawing.</summary>
public class ZoomParentTests
{
    const long Seed = 0x40;

    sealed class Ob
    {
        public long H; public string Name = "";
        public Ob? Parent;            // an attribute's block reference, a vertex's polyline
        public bool InModel;          // sits directly in model space
        public long Def = 1;          // the definition it lives in (when not in model space)
        public List<Ob> Refs = new(); // references of the definition this object lives in
        public bool Throws;           // reading it fails (a proxy, an object open for write)
        public override string ToString() => Name;
    }

    sealed class Fake : IZoomOwners<Ob>
    {
        public List<(long H, List<Ob> Members)> Groups = new();
        public int RefLookups;
        public long Handle(Ob o) => o.H;
        public ZoomOwner OwnerOf(Ob o, out long h)
        {
            if (o.Throws) throw new InvalidOperationException("bad object " + o.Name);
            h = o.Parent?.H ?? o.Def;
            return o.InModel ? ZoomOwner.Space : o.Parent != null ? ZoomOwner.Entity : ZoomOwner.Definition;
        }
        public Ob? ParentEntity(Ob o) => o.Parent;
        public IEnumerable<Ob> ReferencesOfDefinition(Ob o) { RefLookups++; return o.Refs; }
        public IReadOnlyList<long> GroupsOf(Ob o) => Groups.Where(g => g.Members.Contains(o)).Select(g => g.H).ToList();
        public IReadOnlyList<Ob> GroupMembers(long h) => Groups.First(g => g.H == h).Members;
    }

    static Ob M(long h, string n) => new() { H = h, Name = n, InModel = true };
    static string Names(ZoomTarget<Ob>? t) => t == null ? "<none>" : string.Join("+", t.Objects.Select(o => o.Name).OrderBy(x => x));
    static ZoomTarget<Ob>? Resolve(Ob o, Fake g, long seed = Seed) => new ZoomResolver<Ob>(seed, g).Resolve(o);
    static ZoomTarget<Ob>? Newest(IEnumerable<Ob> c, Fake g, long seed = Seed) => ZoomParent.Newest(seed, c, g, out _);

    [Fact]
    public void An_ordinary_object_is_its_own_target() => Assert.Equal("line", Names(Resolve(M(0x50, "line"), new Fake())));

    [Fact]
    public void An_object_from_before_the_run_is_no_target()
    {
        Assert.Null(Resolve(M(0x10, "old line"), new Fake()));
    }

    [Fact]
    public void An_attribute_resolves_to_its_block_reference()
    {
        var ins = M(0x50, "insert");
        var att = new Ob { H = 0x51, Name = "att", Parent = ins };   // higher handle than the insert
        Assert.Equal("insert", Names(Resolve(att, new Fake())));
    }

    [Fact]
    public void Scenario_A_new_vertices_on_an_old_polyline_are_skipped_and_the_new_line_wins()
    {
        // a command draws a LINE and then fits an old polyline: the vertices are the newest objects, their parent is older than the seed
        var oldPoly = M(0x10, "old polyline");
        var v1 = new Ob { H = 0x62, Name = "v1", Parent = oldPoly };
        var v2 = new Ob { H = 0x61, Name = "v2", Parent = oldPoly };
        var line = M(0x55, "new line");
        Assert.Null(Resolve(v1, new Fake()));
        Assert.Equal("new line", Names(Newest(new[] { v1, v2, line }, new Fake())));
    }

    [Fact]
    public void Scenario_B_a_run_that_only_added_attributes_or_vertices_to_old_objects_made_nothing()
    {
        var oldIns = M(0x10, "old insert");
        var att = new Ob { H = 0x51, Name = "att", Parent = oldIns };
        Assert.Null(Newest(new[] { att }, new Fake()));
    }

    [Fact]
    public void A_piece_inside_a_new_definition_resolves_to_the_reference_made_by_the_same_command()
    {
        var oldRef = M(0x10, "old reference");     // before the command
        var newRef = M(0x60, "new reference");
        var newer = M(0x58, "other new reference");
        var piece = new Ob { H = 0x45, Name = "piece", Refs = { oldRef, newer, newRef } };
        Assert.Equal("new reference", Names(Resolve(piece, new Fake())));   // the newest of the command's references
    }

    [Fact]
    public void A_piece_of_a_definition_that_has_no_new_reference_is_not_a_candidate()
    {
        var oldRef = M(0x10, "old reference");
        Assert.Null(Resolve(new Ob { H = 0x45, Name = "piece", Refs = { oldRef } }, new Fake()));   // an old definition changed by the command
        Assert.Null(Resolve(new Ob { H = 0x46, Name = "orphan" }, new Fake()));                      // a definition nothing inserts
    }

    [Fact]
    public void Nested_definitions_resolve_up_to_the_reference_in_model_space()
    {
        var top = M(0x70, "outer reference in model space");
        var inner = new Ob { H = 0x65, Name = "inner reference inside outer definition", Def = 2, Refs = { top } };
        var piece = new Ob { H = 0x50, Name = "piece", Def = 3, Refs = { inner } };
        Assert.Equal("outer reference in model space", Names(Resolve(piece, new Fake())));
    }

    [Fact]
    public void An_attribute_inside_a_nested_definition_goes_through_both_rules()
    {
        var top = M(0x70, "top");
        var inner = new Ob { H = 0x65, Name = "inner", Def = 2, Refs = { top } };
        var host = new Ob { H = 0x50, Name = "host reference in definition", Def = 3, Refs = { inner } };
        var att = new Ob { H = 0x51, Name = "att", Parent = host };
        Assert.Equal("top", Names(Resolve(att, new Fake())));
    }

    [Fact]
    public void A_group_made_by_the_command_resolves_to_the_whole_group()
    {
        var a = M(0x50, "a"); var b = M(0x51, "b"); var c = M(0x52, "c");
        var g = new Fake { Groups = { (0x53, new List<Ob> { a, b, c }) } };
        Assert.Equal("a+b+c", Names(Resolve(c, g)));
        Assert.Equal("a+b+c", Names(Resolve(a, g)));
    }

    [Fact]
    public void A_new_object_added_to_an_old_group_zooms_alone()
    {
        var oldA = M(0x10, "old a"); var oldB = M(0x11, "old b"); var added = M(0x50, "added");
        var g = new Fake { Groups = { (0x12, new List<Ob> { oldA, oldB, added }) } };
        Assert.Equal("added", Names(Resolve(added, g)));
    }

    [Fact]
    public void In_several_new_groups_the_smallest_wins_and_a_tie_goes_to_the_newest_group_and_an_old_group_is_ignored()
    {
        var a = M(0x50, "a"); var b = M(0x51, "b"); var c = M(0x52, "c"); var d = M(0x30, "old");
        var g = new Fake { Groups = { (0x60, new List<Ob> { a, b, c }), (0x61, new List<Ob> { a, b }), (0x5F, new List<Ob> { a, d }) } };
        Assert.Equal("a+b", Names(Resolve(a, g)));
        var tie = new Fake { Groups = { (0x60, new List<Ob> { a, b }), (0x62, new List<Ob> { a, c }), (0x61, new List<Ob> { a, b, c }) } };
        Assert.Equal("a+c", Names(Resolve(a, tie)));
    }

    [Fact]
    public void A_block_reference_in_a_new_group_resolves_to_the_group()
    {
        var ins = M(0x70, "insert"); var other = M(0x71, "other");
        var piece = new Ob { H = 0x50, Name = "piece", Refs = { ins } };
        var g = new Fake { Groups = { (0x72, new List<Ob> { ins, other }) } };
        Assert.Equal("insert+other", Names(Resolve(piece, g)));
    }

    [Fact]
    public void A_vertex_resolves_to_its_new_polyline()
    {
        var pl = M(0x50, "polyline");
        Assert.Equal("polyline", Names(Resolve(new Ob { H = 0x51, Name = "vertex", Parent = pl }, new Fake())));
    }

    [Fact]
    public void Definitions_that_insert_each_other_end_after_the_depth_limit_with_none()
    {
        var a = new Ob { H = 0x50, Name = "a", Def = 1 }; var b = new Ob { H = 0x51, Name = "b", Def = 2 };
        a.Refs.Add(b); b.Refs.Add(a);   // a damaged drawing
        Assert.Null(Resolve(a, new Fake()));
    }

    [Fact]
    public void Model_space_itself_is_never_a_target()
    {
        // an object that reports no owner we can use, and a layout's object, give nothing; so does a chain that never reaches an object in the space
        var g = new Fake();
        var stray = new Ob { H = 0x50, Name = "in paper space", Def = 9 };   // a definition with no references (a layout is "no references")
        Assert.Null(Resolve(stray, g));
        foreach (var t in new[] { Resolve(M(0x50, "x"), g), Newest(new[] { stray }, g) })
            Assert.DoesNotContain("model space", Names(t));
    }

    [Fact]
    public void A_definition_is_looked_at_once_however_many_pieces_it_has()
    {
        var g = new Fake();
        var r = new ZoomResolver<Ob>(Seed, g);
        var oldRef = M(0x10, "old");
        for (int i = 0; i < 1000; i++) Assert.Null(r.Resolve(new Ob { H = 0x100 + i, Name = "piece" + i, Def = 7, Refs = { oldRef } }));
        Assert.Equal(1, g.RefLookups);
        Assert.Equal(1, r.DefinitionsSeen);
    }

    [Fact]
    public void Newest_takes_the_first_candidate_that_resolves()
    {
        var oldRef = M(0x10, "old reference");
        var lost = new Ob { H = 0x80, Name = "piece of an old definition", Refs = { oldRef } };
        var line = M(0x70, "line");
        var ins = M(0x60, "insert");
        var att = new Ob { H = 0x61, Name = "att", Parent = ins };
        Assert.Equal("line", Names(Newest(new[] { lost, line, att, ins }, new Fake())));
        Assert.Equal("insert", Names(Newest(new[] { lost, att, ins }, new Fake())));
        Assert.Null(Newest(new[] { lost }, new Fake()));
        Assert.Null(Newest(Array.Empty<Ob>(), new Fake()));
    }

    [Fact]
    public void A_candidate_that_throws_is_skipped_and_named_and_the_walk_goes_on()
    {
        var bad = new Ob { H = 0x90, Name = "proxy", Throws = true };
        var line = M(0x70, "line");
        var skipped = new List<string>();
        var t = ZoomParent.Newest(Seed, new[] { bad, line }, new Fake(), out var timedOut, null, (c, ex) => skipped.Add(c.Name));
        Assert.Equal("line", Names(t));
        Assert.False(timedOut);
        Assert.Equal(new[] { "proxy" }, skipped);
    }

    [Fact]
    public void An_expired_budget_stops_the_walk_and_says_so()
    {
        var line = M(0x70, "line");
        var t = ZoomParent.Newest(Seed, new[] { line }, new Fake(), out var timedOut, () => true);
        Assert.Null(t);
        Assert.True(timedOut);
    }

    [Fact]
    public void A_group_member_with_absurd_extents_is_left_out_and_the_rest_still_zooms()
    {
        var boxes = new[]
        {
            new ZoomBox(0, 0, 0, 10, 10, 0),
            new ZoomBox(-1e300, 0, 0, 1e300, 0, 0),        // an Xline: an enormous box
            new ZoomBox(double.NaN, 0, 0, 1, 1, 0),         // not a number
            new ZoomBox(20, -5, 0, 30, 5, 0),
        };
        var u = ZoomBox.Union(boxes, 1e15, out var skipped);
        Assert.Equal(2, skipped);
        Assert.Equal(new ZoomBox(0, -5, 0, 30, 10, 0), u);
        Assert.Null(ZoomBox.Union(new[] { boxes[1], boxes[2] }, 1e15, out var s2));
        Assert.Equal(2, s2);
        Assert.Null(ZoomBox.Union(Array.Empty<ZoomBox>(), 1e15, out _));
    }
}
