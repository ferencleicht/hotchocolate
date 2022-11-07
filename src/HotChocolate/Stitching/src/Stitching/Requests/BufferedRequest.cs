using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Stitching.Properties;
using HotChocolate.Types;
using HotChocolate.Utilities;
using Microsoft.Extensions.DependencyInjection;
using static HotChocolate.Stitching.ThrowHelper;

namespace HotChocolate.Stitching.Requests
{
    internal class BufferedRequest
    {
        private BufferedRequest(
            IQueryRequest request,
            DocumentNode document,
            OperationDefinitionNode operation)
        {
            Request = request;
            Document = document;
            Operation = operation;
            Promise = new TaskCompletionSource<IExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IQueryRequest Request { get; }

        public DocumentNode Document { get; }

        public OperationDefinitionNode Operation { get; }

        public TaskCompletionSource<IExecutionResult> Promise { get; }

        public IDictionary<string, string>? Aliases { get; set; }

        public static BufferedRequest Create(IQueryRequest request, ISchema schema)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }

            if (request.Query is null)
            {
                throw new ArgumentException(
                    StitchingResources.BufferedRequest_Create_QueryCannotBeNull,
                    nameof(request));
            }

            var document =
                request.Query is QueryDocument doc
                    ? doc.Document
                    : Utf8GraphQLParser.Parse(request.Query.AsSpan());

            var operation =
                ResolveOperation(document, request.OperationName);

            request = NormalizeRequest(request, operation, schema);

            return new BufferedRequest(request, document, operation);
        }

        internal static OperationDefinitionNode ResolveOperation(
            DocumentNode document,
            string? operationName)
        {
            var operation = operationName is null
                ? document.Definitions.OfType<OperationDefinitionNode>().SingleOrDefault()
                : document.Definitions.OfType<OperationDefinitionNode>().SingleOrDefault(
                    t => operationName.EqualsOrdinal(t.Name?.Value));

            if (operation is null)
            {
                throw BufferedRequest_OperationNotFound(document);
            }

            return operation;
        }

        private static IQueryRequest NormalizeRequest(
            IQueryRequest request,
            OperationDefinitionNode operation,
            ISchema schema)
        {
            if (request.VariableValues is { Count: > 0 })
            {
                var converter = schema.Services.GetTypeConverter();
                var formatter = schema.Services.GetRequiredService<InputFormatter>();
                var builder = QueryRequestBuilder.From(request);

                foreach (var variable in request.VariableValues)
                {
                    if (variable.Value is not IValueNode)
                    {
                        builder.SetVariableValue(
                            variable.Key,
                            RewriteVariable(
                                operation,
                                variable.Key,
                                variable.Value,
                                schema,
                                converter,
                                formatter));
                    }
                }

                return builder.Create();
            }

            return request;
        }

        private static IValueNode RewriteVariable(
            OperationDefinitionNode operation,
            string name,
            object? value,
            ISchema schema,
            ITypeConverter converter,
            InputFormatter inputFormatter)
        {
            var variableDefinition =
                operation.VariableDefinitions.FirstOrDefault(t =>
                    string.Equals(t.Variable.Name.Value, name, StringComparison.Ordinal));

            if (variableDefinition is not null &&
                schema.TryGetType(
                    variableDefinition.Type.NamedType().Name.Value,
                    out INamedInputType namedType))
            {
                var variableType = (IInputType)variableDefinition.Type.ToType(namedType);

                if (value is not null &&
                    !variableType.RuntimeType.IsInstanceOfType(value) &&
                    converter.TryConvert(
                        value.GetType(),
                        variableType.RuntimeType,
                        value,
                        out var converted))
                {
                    value = converted;
                }

                return inputFormatter.FormatValue(
                    value,
                    variableType,
                    Path.New(variableDefinition.Variable.Name.Value));
            }

            throw BufferedRequest_VariableDoesNotExist(name);
        }
    }
}
