using System.ComponentModel;
using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class Game : INotifyPropertyChanged
{
    private string _iconUrl = string.Empty;

    [DataMember] public required string AppId { get; set; }

    [DataMember] public required string Name { get; set; }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}