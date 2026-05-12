using RayTree.Plugins.PostgreSQL.Outbox;

namespace RayTree.Plugins.PostgreSQL.Tests;

public class EntityColumnMapperTests
{
    [Test]
    public void GetColumns_SkipsNotMappedProperty()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        Assert.That(columns.Select(c => c.Property.Name), Does.Not.Contain("Ignored"));
    }

    [Test]
    public void GetColumns_WithColumnName_UsesPrefixedAttributeName()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "Id");
        Assert.That(col.ColumnName, Is.EqualTo("state_custom_id"));
    }

    [Test]
    public void GetColumns_WithColumnName_AffectsOnlySuffix()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "Name");
        Assert.That(col.ColumnName, Is.EqualTo("state_full_name"));
    }

    [Test]
    public void GetColumns_WithoutColumnAttribute_UsesPrefixedSnakeCase()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(TestEntity));
        var col = columns.Single(c => c.Property.Name == "Id");
        Assert.That(col.ColumnName, Is.EqualTo("state_id"));
    }

    [Test]
    public void GetColumns_RequiredOnReferenceType_SetsNotNullable()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "RequiredField");
        Assert.That(col.IsNullable, Is.False);
    }

    [Test]
    public void GetColumns_MaxLengthOnString_EmitsVarchar()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "Bio");
        Assert.That(col.ColumnType, Is.EqualTo("VARCHAR(200)"));
    }

    [Test]
    public void GetColumns_StringLengthOnString_EmitsVarchar()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "Code");
        Assert.That(col.ColumnType, Is.EqualTo("VARCHAR(50)"));
    }

    [Test]
    public void GetColumns_ColumnTypeName_OverridesAutoMapping()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "Metadata");
        Assert.That(col.ColumnType, Is.EqualTo("JSONB"));
    }

    [Test]
    public void GetColumns_RequiredOnNullableValueType_SetsNotNullable()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "RequiredNullableInt");
        Assert.That(col.IsNullable, Is.False);
    }

    [Test]
    public void GetColumns_MaxLengthWinsOverStringLength_WhenBothPresent()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(AnnotatedEntity));
        var col = columns.Single(c => c.Property.Name == "BothLengths");
        Assert.That(col.ColumnType, Is.EqualTo("VARCHAR(100)"));
    }

    [Test]
    public void GetTableName_WithTableAttribute_ReturnsAttributeName()
        => Assert.That(EntityColumnMapper.GetTableName(typeof(AnnotatedEntity)), Is.EqualTo("annotated_entity"));

    [Test]
    public void GetTableName_WithoutTableAttribute_ReturnsSnakeCaseName()
        => Assert.That(EntityColumnMapper.GetTableName(typeof(TestEntity)), Is.EqualTo("test_entity"));

    [Test]
    public void ToPostgresType_IntArray_ReturnsIntegerArray()
        => Assert.That(EntityColumnMapper.ToPostgresType(typeof(int[])), Is.EqualTo("INTEGER[]"));

    [Test]
    public void ToPostgresType_LongArray_ReturnsBigintArray()
        => Assert.That(EntityColumnMapper.ToPostgresType(typeof(long[])), Is.EqualTo("BIGINT[]"));

    [Test]
    public void ToPostgresType_BoolArray_ReturnsBooleanArray()
        => Assert.That(EntityColumnMapper.ToPostgresType(typeof(bool[])), Is.EqualTo("BOOLEAN[]"));

    [Test]
    public void ToPostgresType_GuidArray_ReturnsUuidArray()
        => Assert.That(EntityColumnMapper.ToPostgresType(typeof(Guid[])), Is.EqualTo("UUID[]"));

    [Test]
    public void ToPostgresType_StringArray_ReturnsTextArray()
        => Assert.That(EntityColumnMapper.ToPostgresType(typeof(string[])), Is.EqualTo("TEXT[]"));

    [Test]
    public void GetColumns_IntArrayProperty_MapsToIntegerArrayColumn()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(ArrayEntity));
        var col = columns.Single(c => c.Property.Name == "Tags");
        Assert.That(col.ColumnType, Is.EqualTo("INTEGER[]"));
    }

    [Test]
    public void GetColumns_StringArrayProperty_MapsToTextArrayColumn()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(ArrayEntity));
        var col = columns.Single(c => c.Property.Name == "Labels");
        Assert.That(col.ColumnType, Is.EqualTo("TEXT[]"));
    }

    [Test]
    public void GetColumns_NullableIntArrayProperty_MapsToIntegerArrayColumn()
    {
        var columns = EntityColumnMapper.GetColumns(typeof(ArrayEntity));
        var col = columns.Single(c => c.Property.Name == "OptionalScores");
        Assert.That(col.ColumnType, Is.EqualTo("INTEGER[]"));
    }

}
