using System.Collections;
using FastScriptReload.Tests.Runtime.Integration.CodePatterns;
using UnityEngine.TestTools;

namespace FastScriptReload.Tests.Editor.Integration.CodePatterns
{
    public class AccessingNewFieldsByRefTest : CompileWithRedirectTestBase
    {
        private static readonly string RelativeFilePath = @$"Runtime\Integration\CodePatterns\{nameof(AccessingNewFieldsByRef)}.cs";

        [UnityTest]
        public IEnumerator TestNewFieldsPassedByRef_StandardCompilation_CorrectRewriteCreated()
        {
            TestCompileAndDetour(ResolveFullTestFilePath(RelativeFilePath));

            yield return null;
        }
    }
}
