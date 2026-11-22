using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DicomViewer
{
    // How a server let us download images
    public enum RetrieveMethod { WadoUri, WadoRs }

    // One Packs the app can connect to
    public record PacsServer(string Name, string DicomWebRoot, string WadoUriRoot, RetrieveMethod Retrieve)
    { 
        public override string ToString() => Name; // what the dropdown shows
    }
}
