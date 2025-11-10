using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class Profile
{
    public Profile()
    {
        Name = "New Profile";
        Games = [];
    }

    [DataMember] public string Name { get; set; }

    [DataMember] public List<Game> Games { get; set; }
}