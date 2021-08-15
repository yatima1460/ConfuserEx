using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Confuser.Core;
using Confuser.Core.Project;
using Confuser.UnitTest;
using Xunit;
using Xunit.Abstractions;

namespace MethodOverloading.Test {
	public class MethodOverloadingTest : TestBase {
		public MethodOverloadingTest(ITestOutputHelper outputHelper) : base(outputHelper) { }

		[Theory]
		[MemberData(nameof(MethodOverloadingData))]
		[Trait("Category", "Protection")]
		[Trait("Protection", "rename")]
		[Trait("Issue", "https://github.com/mkaring/ConfuserEx/issues/230")]
		public async Task MethodOverloading(bool shortNames, bool preserveGenericParams) =>
			await Run(
				"MethodOverloading.exe",
				new [] {
					"1",
					"Hello world",
					"object",
					"2",
					"test",
					"5",
					"class",
					"class2",
					"class3",
					"class4",
					"class5",
					"BaseClassVirtualMethod",
					"ClassVirtualMethod",
					"ClassVirtualMethod"
				},
				new SettingItem<IProtection>("rename") {
					["mode"] = "decodable",
					["shortNames"] = shortNames.ToString().ToLowerInvariant(),
					["preserveGenericParams"] = preserveGenericParams.ToString().ToLowerInvariant()
				},
				(shortNames ? "_shortnames" : "_fullnames") + (preserveGenericParams ? "_preserveGenericParams" : ""),
				seed: "seed",
				postProcessAction: outputPath => {
					var symbolsPath = Path.Combine(outputPath, "symbols.map");
					var symbols = File.ReadAllLines(symbolsPath).Select(line => {
						var parts = line.Split('\t');
						return new KeyValuePair<string, string>(parts[0], parts[1]);
					}).ToDictionary(keyValue => keyValue.Key, keyValue => keyValue.Value);

					if (shortNames) {
						Assert.Equal("Class", symbols["_OatkF4GhWlgOakbgdlaLpqEglhm"]);
						Assert.Equal("NestedClass", symbols["_GYHfKMUMLLO9oVLM117IvfCdmUC"]);
						Assert.Equal("OverloadedMethod", symbols["_phF8iy7Y79cwt3EaAFmJzW2bGch"]);
					}
					else {
						Assert.Equal("MethodOverloading.Class", symbols["_iyWU2GdYVZxajP8BQlt8KKTy6qQ"]);
						Assert.Equal("MethodOverloading.Program/NestedClass", symbols["_CZIbNVHU7wPJyGhgOcTnIUsFtC0"]);
						Assert.Equal("MethodOverloading.Program::OverloadedMethod(System.Object[])", symbols["_LzCBuBOSn49xbtKNsjuJxQZPIEW"]);
						Assert.Equal("MethodOverloading.Program::OverloadedMethod(System.String)", symbols["_ywSbkiShk8k3qj7bBrEWEUfs9Km"]);
					}

					return Task.Delay(0);
				}
			);

		public static IEnumerable<object[]> MethodOverloadingData() {
			foreach (var shortNames in new[] {false, true}) {
				foreach (var preserveGenericParams in new[] {false, true}) {
					yield return new object[] {shortNames, preserveGenericParams};
				}
			}
		}
	}
}
