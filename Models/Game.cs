using System.ComponentModel;
using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class Game : INotifyPropertyChanged
{
    private string _iconUrl = string.Empty;
    private bool _isEditing;
    private string _name = string.Empty;

    [DataMember] public required string AppId { get; set; }

    [DataMember]
    public required string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }
    }

    [DataMember] public required string Type { get; set; }

    [DataMember]
    public string IconUrl
    {
        get => _iconUrl;
        set
        {
            if (_iconUrl != value)
            {
                _iconUrl = value;
                OnPropertyChanged(nameof(IconUrl));
            }
        }
    }

    [IgnoreDataMember]
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing != value)
            {
                _isEditing = value;
                OnPropertyChanged(nameof(IsEditing));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}