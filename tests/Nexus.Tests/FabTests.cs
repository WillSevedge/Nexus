using Nexus.Fab;

namespace Nexus.Tests;

public sealed class FabTests
{
    /// <summary>A straight run along X: pipes of <paramref name="lengths"/> feet with a fitting between each, joined end to end.</summary>
    private static List<FabItem> Run(string service, double[] lengths, string joint = "Weld", double startX = 0, string prefix = "")
    {
        var items = new List<FabItem>();
        double x = startX;
        for (int i = 0; i < lengths.Length; i++)
        {
            items.Add(new FabItem
            {
                Key = $"{prefix}P{i}", Kind = FabKind.Pipe, Service = service, Size = "4\"", Material = "CS", Category = "Pipes", Description = "Pipe",
                LengthFt = lengths[i], WeightLb = lengths[i] * 10, MinX = x, MaxX = x + lengths[i], MaxY = 0.4, MaxZ = 0.4,
            });
            x += lengths[i];
            if (i == lengths.Length - 1) break;
            items.Add(new FabItem
            {
                Key = $"{prefix}F{i}", Kind = FabKind.Fitting, Service = service, Size = "4\"", Material = "CS", Category = "Pipe Fittings", Description = "Coupling",
                WeightLb = 5, MinX = x, MaxX = x + 0.5, MaxY = 0.4, MaxZ = 0.4,
            });
            x += 0.5;
        }
        for (int i = 0; i + 1 < items.Count; i++) items[i].Joints.Add(new FabJoint { OtherKey = items[i + 1].Key, Type = joint });
        return items;
    }

    private static SpoolRules Rules(Action<SpoolRules>? set = null)
    {
        var rules = new SpoolRules { MaxLengthFt = 0, Name = "{Service}-{###}" };
        set?.Invoke(rules);
        return rules;
    }

    [Fact]
    public void Pieces_joined_in_the_shop_make_one_spool_named_from_the_service()
    {
        var plan = SpoolPlanner.Plan(Run("CHWS", new[] { 5d, 5, 5 }), Rules());
        var spool = Assert.Single(plan.Spools);
        Assert.Equal(("CHWS-001", 5, "CHWS", 15d), (spool.Name, spool.Items.Count, spool.Service, spool.PipeFt));
        Assert.Equal(16, spool.LongestSideFt);
        Assert.Empty(plan.Loose);
    }

    [Fact]
    public void A_spool_ends_at_a_field_joint_whichever_side_names_it()
    {
        var items = Run("CHWS", new[] { 5d, 5, 5, 5 });
        // The joint between the second fitting and the third pipe is flanged; only the pipe's side says so.
        items[3].Joints.Clear();
        items[4].Joints.Add(new FabJoint { OtherKey = items[3].Key, Type = "150# Flange" });
        var plan = SpoolPlanner.Plan(items, Rules(r => r.FieldJoints.Add("flange")));
        Assert.Equal(new[] { "CHWS-001", "CHWS-002" }, plan.Spools.Select(s => s.Name));
        Assert.Equal(new[] { 4, 3 }, plan.Spools.Select(s => s.Items.Count));
    }

    [Fact]
    public void A_run_too_long_to_ship_is_cut_into_spools_that_fit()
    {
        var plan = SpoolPlanner.Plan(Run("HWS", new[] { 10d, 10, 10, 10 }), Rules(r => r.MaxLengthFt = 21));
        Assert.All(plan.Spools, s => Assert.True(s.LongestSideFt <= 21));
        Assert.Equal(7, plan.Spools.Sum(s => s.Items.Count) + plan.Loose.Count);
        Assert.True(plan.Splits["length"] >= 1);
        Assert.Equal(plan.Spools.Count, plan.Spools.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void Weight_and_piece_limits_cut_spools_too()
    {
        var byWeight = SpoolPlanner.Plan(Run("HWS", new[] { 10d, 10, 10 }), Rules(r => r.MaxWeightLb = 120));
        Assert.All(byWeight.Spools, s => Assert.True(s.WeightLb <= 120));
        var byParts = SpoolPlanner.Plan(Run("HWS", new[] { 2d, 2, 2, 2 }), Rules(r => r.MaxParts = 3));
        Assert.All(byParts.Spools, s => Assert.True(s.Items.Count <= 3));
    }

    [Fact]
    public void Services_stay_apart_valves_can_be_loose_and_a_lone_fitting_is_no_spool()
    {
        var a = Run("CHWS", new[] { 5d, 5 }, prefix: "a");
        var b = Run("CHWR", new[] { 5d, 5 }, startX: 10.5, prefix: "b");
        a[^1].Joints.Add(new FabJoint { OtherKey = b[0].Key, Type = "Weld" }); // joined, but different services
        var valve = new FabItem { Key = "V", Kind = FabKind.Accessory, Service = "CHWR", Description = "Gate Valve", MinX = 21, MaxX = 22 };
        b[^1].Joints.Add(new FabJoint { OtherKey = "V", Type = "Weld" });
        var lone = new FabItem { Key = "L", Kind = FabKind.Fitting, Service = "CHWR", MinX = 40, MaxX = 41 };
        var all = a.Concat(b).Append(valve).Append(lone).ToList();

        var plan = SpoolPlanner.Plan(all, Rules(r => r.AccessoriesAreLoose = true));
        Assert.Equal(new[] { "CHWR-001", "CHWS-001" }, plan.Spools.Select(s => s.Name).OrderBy(n => n));
        Assert.Contains(plan.Loose, i => i.Key == "V");
        Assert.Contains(plan.Loose, i => i.Key == "L");

        var together = SpoolPlanner.Plan(all, Rules(r => r.KeepServicesApart = false));
        Assert.Contains(together.Spools, s => s.Items.Count == 7); // both runs and the valve
    }

    [Fact]
    public void Spools_already_in_the_model_can_be_kept_and_their_names_are_not_reused()
    {
        var items = Run("CHWS", new[] { 5d, 5 }, prefix: "a").Concat(Run("CHWS", new[] { 5d, 5 }, startX: 50, prefix: "b")).ToList();
        foreach (var i in items.Where(i => i.Key.StartsWith("a"))) i.Spool = "CHWS-001";
        var plan = SpoolPlanner.Plan(items, Rules(r => r.KeepExisting = true));
        Assert.Equal(new[] { "CHWS-001", "CHWS-002" }, plan.Spools.Select(s => s.Name).OrderBy(n => n));
        Assert.True(plan.Spools.Single(s => s.Name == "CHWS-001").Existing);
    }

    [Fact]
    public void The_same_model_always_gives_the_same_spools()
    {
        var items = Run("CHWS", new[] { 8d, 8, 8, 8, 8 });
        var first = SpoolPlanner.Plan(items, Rules(r => r.MaxLengthFt = 20));
        var shuffled = SpoolPlanner.Plan(items.AsEnumerable().Reverse().ToList(), Rules(r => r.MaxLengthFt = 20));
        Assert.Equal(first.Spools.Select(s => s.Name + ":" + string.Join(",", s.Items.Select(i => i.Key).OrderBy(k => k))),
            shuffled.Spools.Select(s => s.Name + ":" + string.Join(",", s.Items.Select(i => i.Key).OrderBy(k => k))));
    }

    [Theory]
    [InlineData("{Service}-{###}", 7, "CHWS-007")]
    [InlineData("SP-##", 12, "SP-12")]
    [InlineData("{Level}.{Service}", 3, "L2.CHWS-3")]
    public void Spool_names_follow_the_pattern(string pattern, int number, string expected) =>
        Assert.Equal(expected, SpoolPlanner.Fill(pattern, new Spool { Service = "CHWS", Level = "L2" }, number, numbered: true));

    [Fact]
    public void A_cut_list_groups_equal_cuts_and_nests_them_into_stock_lengths()
    {
        var items = new List<FabItem>();
        void Add(double length, int n, string spool)
        {
            for (int i = 0; i < n; i++)
                items.Add(new FabItem { Key = $"{spool}{length}{i}", Kind = FabKind.Pipe, Service = "CHWS", Material = "CS", Size = "4\"", LengthFt = length, Spool = spool });
        }
        Add(10, 2, "A");
        Add(6, 3, "B");
        Add(4, 2, "A");
        items.Add(new FabItem { Key = "elbow", Kind = FabKind.Fitting, Size = "4\"" }); // not cut
        var plan = CutList.Build(items, new[] { new StockLength { LengthFt = 21 } }, kerfIn: 0);

        Assert.Equal(new[] { (10d, 2), (6d, 3), (4d, 2) }, plan.Lines.Select(l => (l.LengthFt, l.Quantity)));
        Assert.Equal("A", plan.Lines[0].Spools);
        Assert.Equal(46, plan.CutFt);
        Assert.Equal(3, plan.Bars.Count); // 46 ft of cuts cannot come out of two 21 ft bars
        Assert.All(plan.Bars, b => Assert.True(b.Cuts.Sum(c => c.LengthFt) <= 21));
        Assert.Equal(63 - 46, plan.Bars.Sum(b => b.DropFt), 6);
        Assert.Equal(73, plan.YieldPercent);
    }

    [Fact]
    public void The_blade_width_counts_and_pieces_longer_than_stock_are_called_out()
    {
        var items = Enumerable.Range(0, 3).Select(i => new FabItem { Key = "p" + i, Kind = FabKind.Pipe, Material = "CS", Size = "2\"", LengthFt = 7 }).ToList();
        // Three 7 ft cuts are exactly 21 ft: with a blade width between them the third needs a second bar.
        Assert.Single(CutList.Build(items, new[] { new StockLength { LengthFt = 21 } }, kerfIn: 0).Bars);
        Assert.Equal(2, CutList.Build(items, new[] { new StockLength { LengthFt = 21 } }, kerfIn: 0.125).Bars.Count);

        items.Add(new FabItem { Key = "long", Kind = FabKind.Pipe, Material = "CS", Size = "2\"", LengthFt = 24 });
        var plan = CutList.Build(items, new[] { new StockLength { LengthFt = 21 } }, kerfIn: 0);
        Assert.Equal((24d, 1), (plan.TooLong.Single().LengthFt, plan.TooLong.Single().Quantity));
    }

    [Fact]
    public void Stock_lengths_are_chosen_by_material_and_size()
    {
        var stock = new[] { new StockLength { Material = "Copper", LengthFt = 20 }, new StockLength { Size = "6\"", LengthFt = 40 }, new StockLength { LengthFt = 21 } };
        Assert.Equal(20, CutList.StockFor(new FabItem { Material = "Copper Type L", Size = "2\"" }, stock));
        Assert.Equal(40, CutList.StockFor(new FabItem { Material = "CS", Size = "6\"" }, stock));
        Assert.Equal(21, CutList.StockFor(new FabItem { Material = "CS", Size = "2\"" }, stock));
    }

    [Fact]
    public void A_bill_of_material_counts_like_items_and_totals_straights()
    {
        var bom = Bom.Build(Run("CHWS", new[] { 5d, 7.5, 5 }));
        var pipe = bom.Single(l => l.Description == "Pipe");
        Assert.Equal((3, 17.5, "17'-6\""), (pipe.Quantity, pipe.LengthFt, pipe.Length));
        Assert.Equal(2, bom.Single(l => l.Description == "Coupling").Quantity);
    }

    [Theory]
    [InlineData(12.5, "12'-6\"")]
    [InlineData(0.0625, "0'-0 3/4\"")]
    [InlineData(10.0052083333, "10'-0 1/16\"")]
    [InlineData(20, "20'-0\"")]
    public void Lengths_read_in_feet_and_inches(double feet, string text) => Assert.Equal(text, FabMeasure.FeetInches(feet));

    [Fact]
    public void Hangers_are_spread_evenly_within_the_spacing_and_near_each_end()
    {
        Assert.Equal(new[] { 1d, 10.5, 20 }, Placement.HangerPositions(21, 10, 1));
        Assert.Equal(new[] { 1d, 9 }, Placement.HangerPositions(10, 10, 1));
        Assert.Equal(new[] { 1.25 }, Placement.HangerPositions(2.5, 10, 1)); // too short for two: one in the middle
        Assert.Empty(Placement.HangerPositions(0.5, 10, 1));
        var gaps = Placement.HangerPositions(47, 10, 1);
        Assert.All(gaps.Zip(gaps.Skip(1), (a, b) => b - a), gap => Assert.True(gap <= 10 + 1e-9));
    }

    [Fact]
    public void Hanger_rules_match_by_service_and_size_and_sleeves_round_up_to_a_listed_size()
    {
        var rules = new List<HangerRule>
        {
            new() { Service = "Steam", SpacingFt = 8 }, new() { UpToSizeIn = 2, SpacingFt = 10 }, new() { SpacingFt = 12 },
        };
        Assert.Equal(8, Placement.RuleFor(rules, "LP Steam", 6)!.SpacingFt);
        Assert.Equal(10, Placement.RuleFor(rules, "CHWS", 1.5)!.SpacingFt);
        Assert.Equal(12, Placement.RuleFor(rules, "CHWS", 4)!.SpacingFt);

        var sleeves = new SleeveRules { ClearanceIn = 1 };
        Assert.Equal(8, Placement.SleeveDiameterIn(4.5, sleeves));  // 4.5 + 2 = 6.5 → 8
        Assert.Equal(6, Placement.SleeveDiameterIn(4, sleeves));    // exactly 6
        Assert.Equal(30.5, Placement.SleeveDiameterIn(28.3, sleeves)); // past the list: next half inch
    }

    [Fact]
    public void The_project_is_saved_and_read_back_with_its_history_and_scan_codes_name_the_spool()
    {
        var project = new FabProject();
        project.Packages.Add(new FabPackage { Name = "L3-East", Destination = "Level 3", Due = "2026-12-01", Status = "Released" });
        project.Record("seved", "Spool CHWS-001", "Status", "Design", "Released", new DateTime(2026, 11, 15, 9, 30, 0));
        project.Record("seved", "Spool CHWS-001", "Status", "Released", "Released"); // no change: not recorded
        project.Spooling.FieldJoints.Add("Flange");

        var read = FabProject.FromJson(project.ToJson());
        Assert.Equal("Level 3", read.Package("l3-east")!.Destination);
        Assert.Equal(("2026-11-15 09:30", "Design", "Released"), (read.History.Single().When, read.History.Single().From, read.History.Single().To));
        Assert.Equal("Flange", read.Spooling.FieldJoints.Single());
        Assert.True(read.IsLocked("released"));
        Assert.False(read.IsLocked("Design"));
        Assert.Equal(7, FabProject.FromJson("not json").Statuses.Count);

        Assert.Equal("CHWS-001", FabCode.SpoolOf(FabCode.For("CHWS-001", "L3-East")));
        Assert.Equal("CHWS-001", FabCode.SpoolOf("  CHWS-001 "));
    }

    [Fact]
    public void A_mapping_line_writes_one_value_or_text_built_from_several()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Spool"] = "CHWS-001", ["Service"] = "CHWS", ["Size"] = "4\"" };
        string? Value(string name) => values.TryGetValue(name, out var v) ? v : null;

        Assert.Equal("CHWS-001", ParameterMapper.Resolve("Spool", Value));
        Assert.Equal("CHWS-001", ParameterMapper.Resolve(" {spool} ", Value));
        Assert.Equal("CHWS / 4\"", ParameterMapper.Resolve("{Service} / {Size}", Value));

        var missing = new List<string>();
        Assert.Equal("CHWS-", ParameterMapper.Resolve("{Service}-{Item Number}", Value, missing)); // no value: left blank, and said
        Assert.Equal(new[] { "Item Number" }, missing);

        Assert.Equal(new[] { "Service", "Size" }, ParameterMapper.Names("{Service} / {Size} ({service})"));
        Assert.Equal(new[] { "Item Number" }, ParameterMapper.Names("Item Number"));
        Assert.Equal("Length", ParameterMapper.SingleName("{Length}"));
        Assert.Equal("Length", ParameterMapper.SingleName("Length"));
        Assert.Null(ParameterMapper.SingleName("{Length} long"));
    }

    [Fact]
    public void A_saved_mapping_has_one_line_per_parameter_and_no_empty_lines()
    {
        var maps = ParameterMapper.Clean(new[]
        {
            new ParameterMap { From = "Spool", To = "Mark" }, new ParameterMap { From = "Package", To = "mark" },
            new ParameterMap { From = "", To = "Comments" }, new ParameterMap { From = " {Service} ", To = " System Tag " },
        });
        Assert.Equal(new[] { ("Spool", "Mark"), ("{Service}", "System Tag") }, maps.Select(m => (m.From, m.To)));

        var project = new FabProject { Mappings = maps };
        Assert.Equal(2, FabProject.FromJson(project.ToJson()).Mappings.Count);
    }

    [Fact]
    public void Csv_is_quoted_where_needed_and_never_runs_as_a_formula()
    {
        string csv = FabCsv.Write(new[] { "Spool", "Note" }, new[] { new[] { "CHWS-001", "6\" pipe, welded" }, new[] { "=cmd", "-5" } });
        Assert.Equal("Spool,Note\r\nCHWS-001,\"6\"\" pipe, welded\"\r\n'=cmd,-5\r\n", csv);
    }
}
