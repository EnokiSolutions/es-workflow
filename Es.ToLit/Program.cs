using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.IO;

namespace Es.ToLit
{
    public static class Program
    {
        private static readonly CodeGeneratorOptions CodeGeneratorOptions = new CodeGeneratorOptions
        {
            IndentString = string.Empty
        };

        private static string ToLiteral(string input)
        {
            using (var writer = new StringWriter())
            {
                using (var provider = CodeDomProvider.CreateProvider("CSharp"))
                {
                    //var expr = new CodeSnippetExpression(string.Format("\"{0}\"", input));
                    var expr = new CodePrimitiveExpression(input);
                    provider.GenerateCodeFromExpression(expr, writer, CodeGeneratorOptions);
                    return writer.ToString().Replace("\" +" + Environment.NewLine + "\"", string.Empty);
                }
            }
        }

        public static void Main(string[] args)
        {
            try
            {
                for (;;)
                {
                    var line = Console.In.ReadLine();
                    if (line == null)
                        break;

                    Console.Out.WriteLine("sb.AppendLine({0});", ToLiteral(line));
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}