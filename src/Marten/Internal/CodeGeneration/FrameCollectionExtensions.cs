using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Baseline;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten.Schema;
using Marten.Util;

namespace Marten.Internal.CodeGeneration
{
    internal static class FrameCollectionExtensions
    {
        public const string DocumentVariableName = "document";

        public static void StoreInIdentityMap(this FramesCollection frames, DocumentMapping mapping)
        {
            frames.Code("_identityMap[id] = document;");
        }

        public static void StoreTracker(this FramesCollection frames)
        {
            frames.Code("StoreTracker({0}, document);", Use.Type<IMartenSession>());
        }

        public static void Deserialize(this FramesCollection frames, DocumentMapping mapping, int index)
        {
            var documentType = mapping.DocumentType;
            var document = new Variable(documentType, DocumentVariableName);
            if (mapping is DocumentMapping d)
            {
                if (!d.IsHierarchy())
                {
                    frames.Code($@"
{documentType.FullNameInCode()} document;
BLOCK:using (var json = reader.GetTextReader({index}))
    document = _serializer.FromJson<{documentType.FullNameInCode()}>(json);
END
").Creates(document);
                }
                else
                {
                    // Hierarchy path is different
                    frames.Code($@"
{documentType.FullNameInCode()} document;
var typeAlias = reader.GetFieldValue<string>({index + 1});
BLOCK:using (var json = reader.GetTextReader({index}))
    document = ({documentType.FullNameInCode()}) _serializer.FromJson(_mapping.TypeFor(typeAlias), json);
END
").Creates(document);
                }
            }
        }

        public static void MarkAsLoaded(this FramesCollection frames)
        {
            frames.Code($"{{0}}.{nameof(IMartenSession.MarkAsDocumentLoaded)}(id, document);", Use.Type<IMartenSession>());
        }

        public static void DeserializeAsync(this FramesCollection frames, DocumentMapping mapping, int index)
        {
            var documentType = mapping.DocumentType;
            var document = new Variable(documentType, DocumentVariableName);

            if (!mapping.IsHierarchy())
            {
                frames.Code($@"
{documentType.FullNameInCode()} document;
BLOCK:using (var json = reader.GetTextReader({index}))
document = _serializer.FromJson<{documentType.FullNameInCode()}>(json);
END
").Creates(document);
            }
            else
            {
                frames.CodeAsync($@"
{documentType.FullNameInCode()} document;
var typeAlias = await reader.GetFieldValueAsync<string>({index + 1}, {{0}}).ConfigureAwait(false);
BLOCK:using (var json = reader.GetTextReader({index}))
document = ({documentType.FullNameInCode()}) _serializer.FromJson(_mapping.TypeFor(typeAlias), json);
END
", Use.Type<CancellationToken>()).Creates(document);
            }



        }

        /// <summary>
        /// Generates the necessary setter code to set a value of a document.
        /// Handles internal/private setters
        /// </summary>
        /// <param name="frames"></param>
        /// <param name="member"></param>
        /// <param name="variableName"></param>
        /// <param name="documentType"></param>
        /// <param name="generatedType"></param>
        public static void SetMemberValue(this FramesCollection frames, MemberInfo member, string variableName, Type documentType, GeneratedType generatedType)
        {
            if (member is PropertyInfo property)
            {
                if (property.CanWrite)
                {
                    if (property.SetMethod.IsPublic)
                    {
                        frames.SetPublicMemberValue(member, variableName, documentType);
                    }
                    else
                    {
                        var setterFieldName = generatedType.InitializeLambdaSetterProperty(member, documentType);
                        frames.Code($"{setterFieldName}({{0}}, {variableName});", new Use(documentType));
                    }

                    return;
                }
            }
            else if (member is FieldInfo field)
            {
                if (field.IsPublic)
                {
                    frames.SetPublicMemberValue(member, variableName, documentType);
                }
                else
                {
                    var setterFieldName = generatedType.InitializeLambdaSetterProperty(member, documentType);
                    frames.Code($"{setterFieldName}({{0}}, {variableName});", new Use(documentType));
                }

                return;
            }

            throw new ArgumentOutOfRangeException(nameof(member), $"MemberInfo {member} is not valid in this usage. ");
        }

        public static string InitializeLambdaSetterProperty(this GeneratedType generatedType, MemberInfo member, Type documentType)
        {
            var setterFieldName = $"{member.Name}Writer";

            if (generatedType.Setters.All(x => x.PropName != setterFieldName))
            {
                var memberType = member.GetRawMemberType();
                var actionType = typeof(Action<,>).MakeGenericType(documentType, memberType);
                var expression = $"{typeof(LambdaBuilder).GetFullName()}.{nameof(LambdaBuilder.Setter)}<{documentType.FullNameInCode()},{memberType.FullNameInCode()}>(typeof({documentType.FullNameInCode()}).GetProperty(\"{member.Name}\"))";

                var constant = new Variable(actionType, expression);

                var setter = Setter.StaticReadOnly(setterFieldName, constant);

                generatedType.Setters.Add(setter);

            }

            return setterFieldName;
        }

        private static void SetPublicMemberValue(this FramesCollection frames, MemberInfo member, string variableName,
            Type documentType)
        {
            frames.Code($"{{0}}.{member.Name} = {variableName};", new Use(documentType));
        }

        private interface ISetterBuilder
        {
            void Add(GeneratedType generatedType, MemberInfo member, string setterFieldName);
        }

        private class SetterBuilder<TTarget, TMember>: ISetterBuilder
        {
            public void Add(GeneratedType generatedType, MemberInfo member, string setterFieldName)
            {
                var writer = LambdaBuilder.Setter<TTarget, TMember>(member);
                var setter =
                    new Setter(typeof(Action<TTarget, TMember>), setterFieldName)
                    {
                        InitialValue = writer, Type = SetterType.ReadWrite
                    };

                generatedType.Setters.Add(setter);

            }
        }


    }
}
