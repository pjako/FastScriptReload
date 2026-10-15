using UnityEngine;

namespace FastScriptReload.Tests.Runtime.Integration.CodePatterns
{
    public class AccessingNewFieldsByRef : MonoBehaviour
    {
        //<mock-runtime-code-change>// string _newText = "Text";
        //<mock-runtime-code-change>// int _newCounter;

        //newly added fields need to stay variables after rewrite, so they can be passed as ref / out, assigned and incremented
        public void CompileTest()
        {
            //<mock-runtime-code-change>// AppendSuffix(ref _newText);
            //<mock-runtime-code-change>// Increment(ref this._newCounter);
            //<mock-runtime-code-change>// _newCounter += _newText.Length;
            //<mock-runtime-code-change>// _newCounter++;
        }

        private static void AppendSuffix(ref string value) => value += "!";
        private static void Increment(ref int value) => value++;
    }
}
