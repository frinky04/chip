using System.Numerics;
using Chip.Core.Scene;

namespace Chip.Core.Tests;

/// <summary>What several selected entities share, for editing them together in the inspector.</summary>
public class EntityMultiEditTests
{
    static Entity Make(string cls, Vector3 at = default) => EntityClasses.Create(cls, at);

    [Fact]
    public void Common_properties_are_the_ones_every_class_declares_in_the_first_class_order()
    {
        var omni = Make("light_point");
        var spot = Make("light_spot");
        Assert.Equal(["light_color", "light_energy", "shadow_enabled", "omni_range"],
            EntityMultiEdit.CommonProps([omni, Make("light_point")]).Select(p => p.Name));
        Assert.Equal(["light_color", "light_energy", "shadow_enabled"], EntityMultiEdit.CommonProps([omni, spot]).Select(p => p.Name));
        Assert.Empty(EntityMultiEdit.CommonProps([omni, Make("info_target")]));
        // A class the project doesn't define has no properties to share.
        Assert.Empty(EntityMultiEdit.CommonProps([Make("my_custom_thing"), Make("my_custom_thing")]));
    }

    [Fact]
    public void Free_keys_are_every_value_of_an_unknown_class_and_the_undeclared_values_of_a_known_one()
    {
        var omni = Make("light_point");
        omni.Properties["omni_range"] = EntityValues.Of(3.0);
        omni.Properties["leftover"] = EntityValues.Of("x");
        var custom = Make("my_custom_thing");
        custom.Properties["speed"] = EntityValues.Of(2.0);
        custom.Properties["leftover"] = EntityValues.Of("y");
        Assert.Equal(["leftover"], EntityMultiEdit.FreeKeys([omni]));
        Assert.Equal(["leftover", "speed"], EntityMultiEdit.FreeKeys([omni, custom]));
    }

    [Fact]
    public void Mixed_compares_values_structurally_and_per_axis()
    {
        Assert.False(EntityMultiEdit.Mixed([EntityValues.Of(1.0), EntityValues.Of(1.0)]));
        Assert.True(EntityMultiEdit.Mixed([EntityValues.Of(1.0), EntityValues.Of(2.0)]));
        Assert.False(EntityMultiEdit.Mixed([EntityValues.Array(1, 2, 3), EntityValues.Array(1, 2, 3)]));
        Assert.Equal([false, true, false], EntityMultiEdit.MixedAxes([new Vector3(1, 2, 3), new Vector3(1, 5, 3)]));
        Assert.Equal([true, false], EntityMultiEdit.MixedAxes([EntityValues.Array(0, 4), EntityValues.Array(1, 4)], 2));
    }

    [Fact]
    public void Setting_one_axis_keeps_the_others()
    {
        Assert.Equal(new Vector3(1, 9, 3), EntityMultiEdit.WithAxis(new Vector3(1, 2, 3), 1, 9));
        Assert.Equal([7f, 2f, 3f], EntityValues.Vector(EntityMultiEdit.WithAxis(EntityValues.Array(1, 2, 3), 3, 0, 7), 3)!);
        // A value that isn't a vector yet starts from zeros.
        Assert.Equal([0f, 5f], EntityValues.Vector(EntityMultiEdit.WithAxis(EntityValues.Null, 2, 1, 5), 2)!);
    }

    [Fact]
    public void Each_entity_reads_a_shared_property_against_its_own_class()
    {
        var spot = Make("light_spot");
        var shared = EntityClasses.Get("light_point").Prop("light_energy")!;
        Assert.Same(EntityClasses.Get("light_spot").Prop("light_energy"), EntityMultiEdit.SpecFor(spot, shared));
    }
}
