using System;
using System.IO;
using System.Text;
using System.Collections;

using System.Security.Cryptography;

using Brunet;
using Brunet.Dht;

namespace Ipop {
  public class DhtIP {
    public static bool GetIP(FDht _dht, string ipop_namespace, string ip,
                            int leasetime, byte [] brunet_id, ref string password) {
      string key = "dhcp:ipop_namespace:" + ipop_namespace + ":ip:" + ip;
      DhtOp dhtOp = new DhtOp(_dht);
      string output = dhtOp.Create(key, brunet_id, password, leasetime);
      if(output == null) {
        return false;
      }
      password = output;
      return true;
    }
  }
}
