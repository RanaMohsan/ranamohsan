namespace PayNex_POS_B1.Models;

public enum DynamicFieldType
{
    Text,
    Number,
    Decimal,
    CheckBox
}

public class DynamicField
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DynamicFieldType FieldType { get; set; }
    public object? Value { get; set; }
    public bool IsRequired { get; set; }
}
