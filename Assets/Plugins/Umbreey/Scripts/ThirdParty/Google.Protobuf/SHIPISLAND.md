protobuf-31.1 https://github.com/protocolbuffers/protobuf/tree/v31.1/csharp/src/Google.Protobuf

# change log
- 修改c#10才支持的'file-scoped namespace'为传统实现，即分号改为大括号
	FeatureSetDescriptor.cs中namespace Google.Protobuf.Reflection;
	UnsafeCollectionOperations.cs中namespace Google.Protobuf;
	FeatureSetDescriptor.g.cs中namespace Google.Protobuf.Reflection;
