﻿using k8s.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Isol8;
public class ServiceCache : ConcurrentDictionary<ServiceEntry, V1Service>
{

}
