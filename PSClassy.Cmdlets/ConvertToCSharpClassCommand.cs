using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Management.Automation.Language;
namespace PSClassy.Cmdlets
{
    using Core;
    [Cmdlet(VerbsData.ConvertTo, "CSharpClass")]
    public class ConvertToCSharpClassCommand : PSCmdlet
    {
        [Parameter(Mandatory = true, ParameterSetName = "FromFile")]
        public string Path { get; set; }

        [Parameter(Mandatory = true, ParameterSetName = "FromSource")]
        public string TypeDefinition { get; set; }

        [Parameter(Mandatory = false)]
        public string Indentation { get; set; } = new string(' ', 4);

        [Parameter(Mandatory = false)]
        public string NewLine { get; set; } = Environment.NewLine;

        protected override void EndProcessing()
        {
            ScriptBlockAst ast = null;
            ParseError[] errors = null;

            if (ParameterSetName == "FromFile")
            {
                var path = SessionState.Path.GetResolvedProviderPathFromProviderPath(Path, "FileSystem").First();
                ast = Parser.ParseFile(path, out Token[] _, out errors);
            }
            else
            {
                ast = Parser.ParseInput(TypeDefinition, out Token[] _, out errors);
            }

            if (errors.Length > 0)
            {
                var ex = new ParseException(errors);
                ThrowTerminatingError(new ErrorRecord(ex, "ParseErrorsEncountered", ErrorCategory.InvalidArgument, TypeDefinition));
            }

            var classDefinitions = ast.FindAll(ast => ast is TypeDefinitionAst tda && tda.IsClass, false).Cast<TypeDefinitionAst>();
            WriteVerbose($"Found {classDefinitions.Count()} valid class definitions in input");

            foreach (TypeDefinitionAst classDef in classDefinitions)
                WriteObject(Generator.GetCSharpClassDefinition(classDef, ast.UsingStatements.ToArray(), Indentation, NewLine));
        }
    }
}
