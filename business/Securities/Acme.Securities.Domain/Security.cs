using System;

namespace Acme.Securities.Domain;

public class Security
{
    public int Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string Exchange { get; private set; } = string.Empty;
    public string BaseCode { get; private set; } = string.Empty;
    public DateTime UpdatedAt { get; private set; }

    protected Security() { }

    public Security(int id, string code, string name, string type, string exchange, string baseCode, DateTime updatedAt = default)
    {
        Id = id;
        SetCode(code);
        SetName(name);
        SetType(type);
        SetExchange(exchange);
        SetBaseCode(baseCode);
        UpdatedAt = updatedAt;
    }

    public void SetCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("证券代码不能为空", nameof(code));
        Code = code;
    }

    public void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("证券名称不能为空", nameof(name));
        Name = name;
    }

    public void SetType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("证券类型不能为空", nameof(type));
        Type = type;
    }

    public void SetExchange(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentException("交易所不能为空", nameof(exchange));
        Exchange = exchange;
    }

    public void SetBaseCode(string baseCode)
    {
        BaseCode = baseCode ?? string.Empty;
    }

    public void SetUpdatedAt(DateTime value)
    {
        UpdatedAt = value;
    }

    public void UpdateFrom(Security other)
    {
        if (other == null)
            throw new ArgumentNullException(nameof(other));

        SetCode(other.Code);
        SetName(other.Name);
        SetType(other.Type);
        SetExchange(other.Exchange);
        SetBaseCode(other.BaseCode);
        SetUpdatedAt(other.UpdatedAt);
    }
}
