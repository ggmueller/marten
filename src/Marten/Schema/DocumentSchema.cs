using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Baseline;
using Marten.Events;
using Marten.Generation;
using Marten.Schema.Sequences;

namespace Marten.Schema
{
    public class DocumentSchema : IDocumentSchema, IDisposable
    {
        private readonly IConnectionFactory _factory;
        private readonly IDocumentSchemaCreation _creation;

        private readonly ConcurrentDictionary<Type, IDocumentMapping> _mappings =
            new ConcurrentDictionary<Type, IDocumentMapping>();

        private readonly ConcurrentDictionary<Type, IDocumentStorage> _documentTypes =
            new ConcurrentDictionary<Type, IDocumentStorage>();


        public DocumentSchema(StoreOptions options, IConnectionFactory factory, IDocumentSchemaCreation creation)
        {
            _factory = factory;
            _creation = creation;

            StoreOptions = options;

            Sequences = new SequenceFactory(this, _factory, _creation);

            Events = new EventGraph();
        }

        public StoreOptions StoreOptions { get; }


        public void Dispose()
        {
        }

        public IDocumentMapping MappingFor(Type documentType)
        {
            return _mappings.GetOrAdd(documentType, type =>
            {
                return StoreOptions.AllDocumentMappings.SelectMany(x => x.SubClasses)
                    .FirstOrDefault(x => x.DocumentType == type) as IDocumentMapping
                       ?? StoreOptions.MappingFor(type);
            });
        }

        public void EnsureStorageExists(Type documentType)
        {
            StorageFor(documentType);
        }

        public IDocumentStorage StorageFor(Type documentType)
        {
            return _documentTypes.GetOrAdd(documentType, type =>
            {
                var mapping = MappingFor(documentType);
                assertNoDuplicateDocumentAliases();

                var storage = mapping.BuildStorage(this);

                _creation.CreateSchema(this, mapping, () => shouldRegenerate(mapping));

                return storage;
            });
        }

        private void assertNoDuplicateDocumentAliases()
        {
            var duplicates = StoreOptions.AllDocumentMappings.GroupBy(x => x.Alias).Where(x => x.Count() > 1).ToArray();
            if (duplicates.Any())
            {
                var message = duplicates.Select(group =>
                {
                    return
                        $"Document types {@group.Select(x => x.DocumentType.Name).Join(", ")} all have the same document alias '{@group.Key}'. You must explicitly make document type aliases to disambiguate the database schema objects";
                }).Join("\n");

                throw new AmbiguousDocumentTypeAliasesException(message);
            }
        }

        private bool shouldRegenerate(IDocumentMapping mapping)
        {
            if (!DocumentTables().Contains(mapping.TableName)) return true;

            var existing = TableSchema(mapping.TableName);
            var expected = mapping.ToTable(this);

            return !expected.Equals(existing);
        }

        public EventGraph Events { get; }
        public PostgresUpsertType UpsertType => StoreOptions.UpsertType;

        public IEnumerable<string> SchemaTableNames()
        {
            var sql =
                "select table_name from information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ";

            return _factory.GetStringList(sql);
        }

        public string[] DocumentTables()
        {
            return SchemaTableNames().Where(x => x.StartsWith(DocumentMapping.TablePrefix)).ToArray();
        }

        public IEnumerable<string> SchemaFunctionNames()
        {
            return findFunctionNames().ToArray();
        }

        public void Alter(Action<MartenRegistry> configure)
        {
            var registry = new MartenRegistry();
            configure(registry);

            Alter(registry);
        }

        public void Alter<T>() where T : MartenRegistry, new()
        {
            Alter(new T());
        }

        public void Alter(MartenRegistry registry)
        {
            // TODO -- later, latch on MartenRegistry type? May not really matter
            registry.Alter(StoreOptions);
        }

        public ISequences Sequences { get; }

        public void WriteDDL(string filename)
        {
            var sql = ToDDL();
            new FileSystem().WriteStringToFile(filename, sql);
        }

        public string ToDDL()
        {
            var writer = new StringWriter();

            StoreOptions.AllDocumentMappings.Each(x => SchemaBuilder.WriteSchemaObjects(x, this, writer));

            writer.WriteLine(SchemaBuilder.GetText("mt_hilo"));

            return writer.ToString();
        }

        public TableDefinition TableSchema(string tableName)
        {
            if (!DocumentTables().Contains(tableName.ToLower()))
                throw new Exception($"No Marten table exists named '{tableName}'");


            var columns = findTableColumns(tableName);
            var pkName = primaryKeysFor(tableName).SingleOrDefault();

            return new TableDefinition(tableName, pkName, columns);
        }

        public TableDefinition TableSchema(Type documentType)
        {
            return TableSchema(MappingFor(documentType).TableName);
        }

        private string[] primaryKeysFor(string tableName)
        {
            var sql = @"
SELECT a.attname, format_type(a.atttypid, a.atttypmod) AS data_type
FROM pg_index i
JOIN   pg_attribute a ON a.attrelid = i.indrelid
                     AND a.attnum = ANY(i.indkey)
WHERE i.indrelid = ?::regclass
AND i.indisprimary; 
";

            return _factory.GetStringList(sql, tableName).ToArray();
        }

        private IEnumerable<TableColumn> findTableColumns(string tableName)
        {
            Func<DbDataReader, TableColumn> transform = r => new TableColumn(r.GetString(0), r.GetString(1));

            var sql =
                "select column_name, data_type from information_schema.columns where table_name = ? order by ordinal_position";
            return _factory.Fetch(sql, transform, tableName);
        }


        private IEnumerable<string> findFunctionNames()
        {
            var sql = @"
SELECT routine_name
FROM information_schema.routines
WHERE specific_schema NOT IN ('pg_catalog', 'information_schema')
AND type_udt_name != 'trigger';
";

            return _factory.GetStringList(sql);
        }
    }

    [Serializable]
    public class AmbiguousDocumentTypeAliasesException : Exception
    {
        public AmbiguousDocumentTypeAliasesException(string message) : base(message)
        {
        }

        protected AmbiguousDocumentTypeAliasesException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}