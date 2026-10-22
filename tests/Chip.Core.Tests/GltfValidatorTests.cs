using System.Diagnostics;
using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Xunit.Abstractions;

namespace Chip.Core.Tests;

/// <summary>
/// Runs the Khronos glTF validator over representative exports when it is available. Set <c>CHIP_GLTF_VALIDATOR</c> to a
/// folder where <c>npm install gltf-validator</c> was run (and have <c>node</c> on PATH); otherwise the test only writes
/// the files and says where they are, so the validator can be run by hand.
/// </summary>
public class GltfValidatorTests(ITestOutputHelper output)
{
    const string Script = """
        const fs = require('fs'), path = require('path'), validator = require('gltf-validator');
        (async () => {
          let failed = false;
          for (const file of process.argv.slice(2)) {
            const report = await validator.validateBytes(new Uint8Array(fs.readFileSync(file)), {
              uri: file,
              externalResourceFunction: (uri) => new Promise((res, rej) => fs.readFile(path.resolve(path.dirname(file), decodeURIComponent(uri)),
                (err, data) => err ? rej(err.toString()) : res(new Uint8Array(data)))),
            });
            const i = report.issues;
            console.log(`${path.basename(file)}: ${i.numErrors} errors, ${i.numWarnings} warnings`);
            for (const m of i.messages.filter(m => m.severity <= 1)) console.log(`  ${m.code} ${m.pointer || ''} ${m.message}`);
            if (i.numErrors > 0 || i.numWarnings > 0) failed = true;
          }
          process.exit(failed ? 1 : 0);
        })();
        """;

    static Document Sample(out MaterialLibrary lib, string dir)
    {
        var png = Path.Combine(dir, "tex", "brick.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        File.WriteAllBytes(png, MaterialLibrary.EncodePng(4, 4, Enumerable.Repeat((byte)180, 64).ToArray()));
        lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("brick", png, 4, 4));
        var doc = new Document();
        var a = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(4, 1, 4), "brick", TextureMapping.Default));
        foreach (var e in a.Edges()) a.SoftEdges.Add(e);
        a.Faces[0].Material = "clip";
        a.Faces[1].Material = "trigger";
        doc.Add(MeshOps.CreateBox(new Vector3(5, 0, 0), new Vector3(6, 1, 1), "missing_tex", TextureMapping.Default)).Collision = CollisionMode.ConvexOnly;
        doc.Add(MeshOps.CreateBox(new Vector3(7, 0, 0), new Vector3(8, 1, 1), "brick", TextureMapping.Default)).Collision = CollisionMode.Only;
        var light = doc.Add(new Entity { ClassName = "light_spot", Position = new Vector3(1, 3, 1), Rotation = new Vector3(-60, 30, 0) });
        light.Properties["light_energy"] = EntityValues.Of(2.0);
        doc.Add(new Entity { ClassName = "info_player_start", Name = "spawn" });
        doc.Export.MaterialRemap["brick"] = "res://materials/brick.tres";
        return doc;
    }

    [Fact]
    public void Exports_pass_the_khronos_validator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chip-validator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "out"));
        var doc = Sample(out var lib, dir);
        var files = new List<string>();
        string Write(string name, Document d)
        {
            var path = Path.Combine(dir, "out", name);
            GlbExporter.Export(d, lib, path);
            files.Add(path);
            return path;
        }
        Write("embedded.glb", doc);
        var external = doc.Clone();
        external.Export.EmbedTextures = false;
        Write("external.glb", external);
        var entitiesOnly = new Document();
        entitiesOnly.Add(new Entity { ClassName = "info_target" });
        Write("entities.glb", entitiesOnly);
        Write("empty.glb", new Document());
        var tessellated = doc.Clone(); // baked collision (the default) plus tessellated render meshes
        tessellated.Export.Tessellate = true;
        tessellated.Export.TessellateSpacing = 0.5f;
        Write("tessellated.glb", tessellated);
        var hulls = doc.Clone();
        hulls.Export.CollisionHulls = true;
        Write("hulls.glb", hulls);

        var validator = Environment.GetEnvironmentVariable("CHIP_GLTF_VALIDATOR");
        if (string.IsNullOrEmpty(validator) || !Directory.Exists(Path.Combine(validator, "node_modules", "gltf-validator")))
        {
            output.WriteLine($"CHIP_GLTF_VALIDATOR not set: validate {Path.Combine(dir, "out")} by hand (npx gltf-validator <file>).");
            return;
        }
        var script = Path.Combine(dir, "validate.js");
        File.WriteAllText(script, Script);
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        foreach (var f in files) psi.ArgumentList.Add(f);
        psi.Environment["NODE_PATH"] = Path.Combine(validator, "node_modules");
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        output.WriteLine(stdout);
        Assert.True(p.ExitCode == 0, stdout + stderr);
    }
}
